//
// Copyright (c) 2010-2025 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.Wireless
{
    public class NRF52840_Radio : BasicDoubleWordPeripheral, IRadio, IKnownSize, INRFEventProvider
    {
        public NRF52840_Radio(IMachine machine) : base(machine)
        {
            IRQ = new GPIO();
            interruptManager = new InterruptManager<Events>(this, IRQ, "RadioIrq");
            shorts = new Shorts();
            events = new IFlagRegisterField[32];
            rxBuffer = new ConcurrentQueue<(byte[] Frame, IRadio Sender, int SenderChannel)>();
            DefineRegisters();
            Reset();
        }

        public override void Reset()
        {
            radioState = State.Disabled;
            packetInProgress = false;
            disablePending = false;
            interruptManager.Reset();
            base.Reset();
            addressPrefixes = new byte[8];
            while(rxBuffer.TryDequeue(out var _)) { }
        }

        public void FakePacket()
        {
            ReceiveFrame(new byte[] { 0xD6, 0xBE, 0x89, 0x8E, 0x60, 0x11, 0xFF, 0xFF, 0xFF, 0xFF, 0x0, 0xC0, 0x2, 0x1, 0x6, 0x7, 0x3, 0xD, 0x18, 0xF, 0x18, 0xA, 0x18, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0, 0x0 }, null);
        }

        public void ReceiveFrame(byte[] frame, IRadio sender)
        {
            var senderChannel = sender?.Channel ?? Channel;
            if(radioState != State.RxIdle)
            {
                rxBuffer.Enqueue((frame, sender, senderChannel));
                // Schedule repeated retries so buffered frames are
                // processed once the radio transitions to RxIdle.  The
                // BLE controller may take several quantums to re-enable
                // the radio via PPI after disabling it.
                ScheduleBufferRetry();
                return;
            }
            var addressLength = (int)baseAddressLength.Value + 1;
            var headerLengthInAir = HeaderLengthInAir();
            var headerLengthInRAM = HeaderLengthInRAM();

            var dataAddress = (uint)packetPointer.Value;

            // Write the on-air header bytes to RAM. When headerLengthInRAM > headerLengthInAir
            // (S1INCL=1 with S1LEN=0), the extra S1 padding byte in RAM must not consume
            // a payload byte from the frame.
            sysbus.WriteBytes(frame, address: dataAddress, startingIndex: addressLength, count: headerLengthInAir);
            if(headerLengthInRAM > headerLengthInAir)
            {
                // Zero-fill the S1 padding byte(s) between header and payload in RAM
                for(int i = 0; i < headerLengthInRAM - headerLengthInAir; i++)
                {
                    sysbus.WriteByte(dataAddress + (uint)headerLengthInAir + (uint)i, 0);
                }
            }

            var payloadLength = Math.Min(frame[addressLength + (int)s0Length.Value], (byte)maxPacketLength.Value);
            sysbus.WriteBytes(frame, address: (ulong)(dataAddress + headerLengthInRAM), startingIndex: addressLength + headerLengthInAir, count: payloadLength);


            var timeSource = machine.LocalTimeSource;

            Timer0?.WriteDoubleWord(0x000, 0x1);  // TASKS_START
            SetEvent(Events.Address);
            Timer0?.WriteDoubleWord(0x044, 0x1);  // TASKS_CAPTURE[1]

            if(shorts.AddressRSSIStart.Value)
            {
                SetEvent(Events.RSSIEnd);
            }
            if(shorts.AddressBitCountStart.Value)
            {
                SetEvent(Events.BitCountMatch);
            }

            packetInProgress = true;
            disablePending = false;

            SetEvent(Events.Payload);
            SetEvent(Events.End);
            SetEvent(Events.PHYEnd);
            SetEvent(Events.CRCOk);
            Timer0?.WriteDoubleWord(0x048, 0x1);  // TASKS_CAPTURE[2]

            // Delay shorts execution to give the CPU ISR time to process
            // the received PDU and prepare the TX response.  In real
            // hardware the BLE controller uses T_IFS (150 µs) between RX
            // end and TX start; the CPU ISR runs during this window.
            var shortsTimeStamp = new TimeStamp(
                timeSource.ElapsedVirtualTime + TimeInterval.FromMicroseconds(10),
                timeSource.Domain);

            timeSource.ExecuteInSyncedState(_ =>
            {
                packetInProgress = false;
                if(disablePending)
                {
                    disablePending = false;
                    Disable();
                }
                else if(shorts.EndDisable.Value || shorts.PHYEndDisable.Value)
                {
                    Disable();
                }
                else if(shorts.EndStart.Value || shorts.PHYEndStart.Value)
                {
                    Start();
                }
            }, shortsTimeStamp);
        }

        public int Channel
        {
            get
            {
                return bluetoothLEChannelMap.TryGetValue(Frequency, out var result)
                    ? result
                    : -1;
            }

            set
            {
                throw new RecoverableException("Setting channel manually is not supported");
            }
        }

        public long Size => 0x1000;

        public uint Frequency => (frequencyMap.Value ? 2360 : 2400U) + (uint)frequency.Value;

        public GPIO IRQ { get; }

        public event Action<IRadio, byte[]> FrameSent;

        public event Action<uint> EventTriggered;

        private void DefineTask(Registers register, Action callback, string name)
        {
            register.Define(this, name: name)
                .WithFlag(0, FieldMode.Write, writeCallback: (_, value) => { if(value) callback(); })
                .WithReservedBits(1, 31)
            ;
        }

        private void DefineEvent(Registers register, Action callbackOnSet, Events @event, string name)
        {
            register.Define(this, name: name)
                .WithFlag(0, out events[(int)@event], writeCallback: (_, value) =>
                {
                    if(value)
                    {
                        callbackOnSet();
                    }
                    else
                    {
                        interruptManager.SetInterrupt(@event, false);
                    }
                })
                .WithReservedBits(1, 31)
            ;
        }

        private void DefineRegisters()
        {
            DefineTask(Registers.TxEnable, TxEnable, "TASKS_TXEN");

            DefineTask(Registers.RxEnable, RxEnable, "TASKS_RXEN");

            DefineTask(Registers.Start, Start, "TASKS_START");

            DefineTask(Registers.Disable, Disable, "TASKS_DISABLE");

            DefineEvent(Registers.Ready, () => this.Log(LogLevel.Error, "Trying to trigger READY event, not supported"), Events.Ready, "EVENTS_READY");
            DefineEvent(Registers.AddressSentOrReceived, () => this.Log(LogLevel.Error, "Trying to trigger ADDRESS event, not supported"), Events.Address, "EVENTS_ADDRESS");
            DefineEvent(Registers.PayloadSentOrReceived, () => this.Log(LogLevel.Error, "Trying to trigger PAYLOAD event, not supported"), Events.Payload, "EVENTS_PAYLOAD");
            DefineEvent(Registers.PacketSentOrReceived, () => this.Log(LogLevel.Error, "Trying to trigger END event, not supported"), Events.End, "EVENTS_END");
            DefineEvent(Registers.BitCounterMatch, () => this.Log(LogLevel.Error, "Trying to trigger BCMATCH event, not supported"), Events.BitCountMatch, "EVENTS_BCMATCH");
            DefineEvent(Registers.RSSIEnd, () => this.Log(LogLevel.Error, "Trying to trigger RSSIEnd event, not supported"), Events.RSSIEnd, "EVENTS_RSSIEND");
            DefineEvent(Registers.CRCOk, () => this.Log(LogLevel.Error, "Trying to trigger CRCOk event, not supported"), Events.CRCOk, "EVENTS_CRCOK");

            DefineEvent(Registers.RadioDisabled, Disable, Events.Disabled, "EVENTS_DISABLED");

            DefineEvent(Registers.TxReady, TxEnable, Events.TxReady, "EVENTS_TXREADY");

            DefineEvent(Registers.RxReady, RxEnable, Events.RxReady, "EVENTS_RXREADY");

            DefineEvent(Registers.PHYEnd, () => {}, Events.PHYEnd, "EVENTS_PHYEND");

            DefineEvent(Registers.DeviceMatch, () => {}, Events.DeviceAddressMatch, "EVENTS_DEVMATCH");
            DefineEvent(Registers.DeviceMiss, () => {}, Events.DeviceAddressMiss, "EVENTS_DEVMISS");

            // Notice: while the whole Shorts register is implemented, we don't necessarily
            // support all the mentioned events and tasks
            Registers.Shorts.Define(this)
                .WithFlag(0, out shorts.ReadyStart, name: "READY_START")
                .WithFlag(1, out shorts.EndDisable, name: "END_DISABLE")
                .WithFlag(2, out shorts.DisabledTxEnable, name: "DISABLED_TXEN")
                .WithFlag(3, out shorts.DisabledRxEnable, name: "DISABLED_RXEN")
                .WithFlag(4, out shorts.AddressRSSIStart, name: "ADDRESS_RSSISTART")
                .WithFlag(5, out shorts.EndStart, name: "END_START")
                .WithFlag(6, out shorts.AddressBitCountStart, name: "ADDRESS_BCSTART")
                .WithFlag(8, out shorts.DisabledRSSIStop, name: "DISABLED_RSSISTOP")
                .WithFlag(11, out shorts.RxReadyCCAStart, name: "RXREADY_CCASTART")
                .WithFlag(12, out shorts.CCAIdleTxEnable, name: "CCAIDLE_TXEN")
                .WithFlag(13, out shorts.CCABusyDisable, name: "CCABUSY_DISABLE")
                .WithFlag(14, out shorts.FrameStartBitCountStart, name: "FRAMESTART_BCSTART")
                .WithFlag(15, out shorts.ReadyEnergyDetectStart, name: "READY_EDSTART")
                .WithFlag(16, out shorts.EnergyDetectEndDisable, name: "EDEND_DISABLE")
                .WithFlag(17, out shorts.CCAIdleStop, name: "CCAIDLE_STOP")
                .WithFlag(18, out shorts.TxReadyStart, name: "TXREADY_START")
                .WithFlag(19, out shorts.RxReadyStart, name: "RXREADY_START")
                .WithFlag(20, out shorts.PHYEndDisable, name: "PHYEND_DISABLE")
                .WithFlag(21, out shorts.PHYEndStart, name: "PHYEND_START")
                .WithReservedBits(22, 10)
            ;

            RegistersCollection.AddRegister((long)Registers.InterruptEnable,
                interruptManager.GetInterruptEnableSetRegister<DoubleWordRegister>());

            RegistersCollection.AddRegister((long)Registers.InterruptDisable,
                interruptManager.GetInterruptEnableClearRegister<DoubleWordRegister>());

            Registers.CRCStatus.Define(this)
               .WithFlag(0, name: "CRCSTATUS", valueProviderCallback: _ => true) // we assume here that CRCs are always ok
               .WithReservedBits(1, 31)
            ;

            Registers.RxMatch.Define(this, name: "RXMATCH")
                .WithValueField(0, 3, FieldMode.Read, name: "RXMATCH")
                .WithReservedBits(3, 29)
            ;

            Registers.RxCRC.Define(this, name: "RXCRC")
                .WithValueField(0, 24, FieldMode.Read, name: "RXCRC")
                .WithReservedBits(24, 8)
            ;

            Registers.PacketPointer.Define(this)
                .WithValueField(0, 32, out packetPointer, name: "PACKETPTR")
            ;

            Registers.Frequency.Define(this, name: "FREQUENCY")
                .WithValueField(0, 7, out frequency, name: "FREQUENCY")
                .WithReservedBits(7, 1)
                .WithFlag(8, out frequencyMap, name: "MAP")
                .WithReservedBits(9, 23)
            ;

            Registers.TxPower.Define(this, name: "TXPOWER")
                .WithValueField(0, 8, name: "TXPOWER") // just RW
                .WithReservedBits(8, 24)
            ;

            Registers.Mode.Define(this, name: "MODE")
                .WithValueField(0, 4, name: "MODE") // just RW
                .WithReservedBits(4, 24)
            ;

            Registers.PacketConfiguration0.Define(this, name: "PCNF0")
                .WithValueField(0, 4, out lengthFieldLength, name: "LFLEN")
                .WithReservedBits(4, 4)
                .WithValueField(8, 1, out s0Length, name: "S0LEN")
                .WithReservedBits(9, 7)
                .WithValueField(16, 4, out s1Length, name: "S1LEN")
                .WithFlag(20, out s1Include, name: "S1INCL")
                .WithReservedBits(21, 1)
                .WithValueField(22, 2, out codeIndicatorLength, name: "CILEN")
                .WithTag("PLEN", 24, 2)
                .WithFlag(26, out crcIncludedInLength, name: "CRCINC")
                .WithReservedBits(27, 2)
                .WithValueField(29, 2, out termLength, name: "TERMLEN")
                .WithReservedBits(31, 1)
            ;

            Registers.PacketConfiguration1.Define(this, name: "PCNF1")
                .WithValueField(0, 8, out maxPacketLength, name: "MAXLEN")
                .WithValueField(8, 8, out staticLength, name: "STATLEN")
                .WithValueField(16, 3, out baseAddressLength, name: "BALEN")
                .WithReservedBits(19, 5)
                .WithFlag(24, name: "ENDIAN")
                .WithFlag(25, name: "WHITEEN")
                .WithReservedBits(26, 6)
            ;

            Registers.BaseAddress0.Define(this)
                .WithValueField(0, 32, out baseAddress0, name: "BASE0")
            ;

            Registers.BaseAddress1.Define(this)
                .WithValueField(0, 32, out baseAddress1, name: "BASE1")
            ;

            Registers.Prefix0.Define(this, name: "PREFIX0")
                .WithValueFields(0, 8, 4, writeCallback: (i, _, value) => addressPrefixes[i] = (byte)value, name: "AP")
            ;

            Registers.Prefix1.Define(this, name: "PREFIX1")
                .WithValueFields(0, 8, 4, writeCallback: (i, _, value) => addressPrefixes[4 + i] = (byte)value, name: "AP")
            ;

            Registers.TxAddress.Define(this, name: "TXADDRESS")
                .WithValueField(0, 3, out txAddress, name: "TXADDRESS")
                .WithReservedBits(3, 29)
            ;

            Registers.RxAddresses.Define(this, name: "RXADDRESSES")
                .WithFlags(0, 8, out rxAddressEnabled, name: "ADDR")
            ;

            Registers.CRCConfiguration.Define(this, name: "CRCCNF")
                .WithValueField(0, 2, out crcLength, name: "LEN")
                .WithReservedBits(2, 6)
                .WithEnumField(8, 2, out crcSkipAddress, name: "SKIPADDR")
                .WithReservedBits(10, 21)
            ;

            Registers.CRCPolynomial.Define(this, name: "CRCPOLY")
                .WithValueField(0, 24, out crcPolynomial, name: "CRCPOLY")
                .WithReservedBits(24, 8)
            ;

            Registers.CRCInitialValue.Define(this, name: "CRCINIT")
                .WithValueField(0, 24, out crcInitialValue, name: "CRCINIT")
                .WithReservedBits(24, 8)
            ;

            Registers.RSSISample.Define(this, name: "RSSISAMPLE")
                .WithValueField(0, 32, valueProviderCallback: _ => DefaultRSSISample, name: "RSSISAMPLE");
            ;

            Registers.State.Define(this, name: "STATE")
                .WithEnumField<DoubleWordRegister, State>(0, 4, FieldMode.Read, valueProviderCallback: _ => radioState, name: "STATE")
                .WithReservedBits(4, 28)
            ;

            Registers.DataWhiteningInitialValue.Define(this, name: "DATAWHITEIV")
                .WithValueField(0, 7, name: "DATAWHITEIV")
                .WithReservedBits(7, 25)
            ;

            Registers.BitCounterCompare.Define(this, name: "BCC")
                .WithValueField(0, 32, out bitCountCompare)
            ;

            Registers.PDUStatus.Define(this, name: "PDUSTAT")
                .WithValueField(0, 32, FieldMode.Read, name: "PDUSTAT")
            ;

            Registers.DeviceAddressMatchIndex.Define(this, name: "DAI")
                .WithValueField(0, 3, FieldMode.Read, name: "DAI")
                .WithReservedBits(3, 29)
            ;

            Registers.DeviceAddressMatchConfiguration.Define(this, name: "DACNF")
                .WithValueField(0, 16, name: "ENA")
                .WithValueField(16, 16, name: "TXADD")
            ;

            Registers.ModeConfiguration0.Define(this, 0x200)
                .WithFlag(0, name: "RU")
                .WithReservedBits(1, 7)
                .WithValueField(8, 2, name: "DTX")
                .WithReservedBits(10, 22)
            ;

            Registers.CCAControl.Define(this, 0x052D0000, name: "CCACTRL")
                .WithEnumField(0, 3, out ccaMode, name: "CCAMODE")
                .WithReservedBits(3, 5)
                .WithTag("CCAEDTHRES", 8, 8)
                .WithTag("CCACORRTHRES", 16, 8)
                .WithTag("CCACORRCNT", 24, 8)
            ;

            Registers.DFESwitch.Define(this, name: "DFESWITCH")
                .WithValueField(0, 32, name: "DFESWITCH")
            ;

            Registers.DFEGPIOSelect0.Define(this, name: "DFEGPIO0")
                .WithValueField(0, 32, name: "DFEGPIO0")
            ;

            Registers.DFEGPIOSelect1.Define(this, name: "DFEGPIO1")
                .WithValueField(0, 32, name: "DFEGPIO1")
            ;

            Registers.CTEInlineConfig.Define(this, name: "CTEINLINECONF")
                .WithValueField(0, 32, name: "CTEINLINECONF")
            ;

            Registers.DFEControl1.Define(this, name: "DFECTRL1")
                .WithValueField(0, 32, name: "DFECTRL1")
            ;

            Registers.DFEControl2.Define(this, name: "DFECTRL2")
                .WithValueField(0, 32, name: "DFECTRL2")
            ;

            Registers.SwitchPattern.Define(this, name: "SWITCHPATTERN")
                .WithValueField(0, 32, name: "SWITCHPATTERN")
            ;

            Registers.ClearPattern.Define(this, name: "CLEARPATTERN")
                .WithValueField(0, 32, name: "CLEARPATTERN")
            ;

            Registers.DFEMode.Define(this, name: "DFEMODE")
                .WithValueField(0, 32, name: "DFEMODE")
            ;

            Registers.PowerControl.Define(this, 1, name: "POWER")
                //TODO: radio should be disabled with powerOn == false
                .WithFlag(0, out powerOn, changeCallback: (_, value) => { if(!value) Reset(); }, name: "POWER")
                .WithReservedBits(1, 31)
            ;
        }

        private void LogUnhandledShort(IFlagRegisterField field, string shortName)
        {
            if(field.Value)
            {
                this.Log(LogLevel.Error, $"Unhandled short {shortName}!");
            }
        }

        private void SetEvent(Events @event)
        {
            interruptManager.SetInterrupt(@event);
            events[(int)@event].Value = true;
            EventTriggered?.Invoke((uint)@event * 4 + 0x100);
        }

        private void Disable()
        {
            if(packetInProgress)
            {
                disablePending = true;
                return;
            }
            radioState = State.Disabled;
            SetEvent(Events.Disabled);
            if(shorts.DisabledRxEnable.Value)
            {
                RxEnable();
            }
            else if(shorts.DisabledTxEnable.Value)
            {
                TxEnable();
            }
            // If neither short handles the transition and there are
            // buffered frames waiting, force-enable RX immediately to
            // process them.  The BLE controller uses PPI to automate
            // this transition, but Renode's PPI timing delays cause
            // TASKS_RXEN to arrive too late.
            if(radioState == State.Disabled && !rxBuffer.IsEmpty)
            {
                RxEnable();
            }
        }

        // These comments sum up some details gathered from the documentation.
        //
        // Packet:
        // preamble, address (base + prefix), CI, TERM1, S0, LENGTH, S1, PAYLOAD, STATIC PAYLOAD (from STATLEN), CRC, TERM2

        // Packet data stored in RAM:
        // S0, Length, S1, Payload

        // CRC:
        // start with address, start with CI or start with L1

        // S0 + Length + S1 + payload <= 258 bytes

        // transmit sequence
        // TXEn -> State.TxRampup -> Event.Ready -> State.TxIdle.  Start -> State.Tx. Sending data: P, A, --> Event.Address,  S0, L, S1, Payload, --> Event.Payload, CRC, --> Event.End (data finished) -> State.TxIdle.
        // Disable -> State.TxDisable -> Event.Disabled

        // transmit sequence with shortcuts
        // TXEn -> State.TxRampup -> Event.Ready shortcut to Start, Event.End shortcut to Disable

        // transmit multiple packets
        // no shortcut end->disable. After Event.End, send start

        // receive sequence
        // RXEn -> State.RxRampup -> Event.Ready -> State.RxIdle. Start->State.Rx. Receiving data: P, A --> Event.Address, s0, l, s1, payload  --> Event.Payload, crc -->Event.End (data finished) --> State.RxIdle
        // Disable --> State.RxDisable -> Event.Disabled

        private void TxEnable()
        {
            radioState = State.TxIdle;
            // we're skipping rampup, it's instant

            SetEvent(Events.Ready);
            SetEvent(Events.TxReady);

            if(shorts.ReadyStart.Value || shorts.TxReadyStart.Value)
            {
                Start();
            }
            LogUnhandledShort(shorts.ReadyEnergyDetectStart, nameof(shorts.ReadyEnergyDetectStart));
        }

        private void RxEnable()
        {
            radioState = State.RxIdle;
            // we're skipping rampup, it's instant

            SetEvent(Events.Ready);
            SetEvent(Events.RxReady);

            if(shorts.ReadyStart.Value || shorts.RxReadyStart.Value)
            {
                Start();
            }
            else if(!rxBuffer.IsEmpty)
            {
                Start();
            }
            LogUnhandledShort(shorts.ReadyEnergyDetectStart, nameof(shorts.ReadyEnergyDetectStart));
        }

        private void Start()
        {
            // common task for both reception and transmission
            if(radioState == State.TxIdle)
            {
                SendPacket();
            }
            else if(radioState == State.RxIdle)
            {
                // Find the first buffered frame compatible with the
                // radio's current channel.  Advertising channels (37-39)
                // Accept any buffered frame whose channel is in the same
                // group as the radio's current channel.  Both advertising
                // channels (37-39) and data channels (0-36) are treated
                // as interchangeable within their group because simulation
                // timing drift causes the sender's channel to differ from
                // the receiver's at deferred-delivery time, even though
                // both radios were on the same channel during the actual
                // connection event.
                var currentChannel = Channel;
                bool onAdvChannel = currentChannel >= 37 && currentChannel <= 39;
                bool onDataChannel = currentChannel >= 0 && currentChannel <= 36;
                var count = rxBuffer.Count;
                for(int i = 0; i < count; i++)
                {
                    if(!rxBuffer.TryDequeue(out var entry))
                    {
                        break;
                    }
                    bool senderOnAdv = entry.SenderChannel >= 37 && entry.SenderChannel <= 39;
                    bool senderOnData = entry.SenderChannel >= 0 && entry.SenderChannel <= 36;
                    if(entry.SenderChannel == currentChannel
                        || (onAdvChannel && senderOnAdv)
                        || (onDataChannel && senderOnData))
                    {
                        ReceiveFrame(entry.Frame, entry.Sender);
                        return;
                    }
                    rxBuffer.Enqueue(entry);
                }
            }
            else
            {
                this.Log(LogLevel.Error, "Triggered the Start task in an unexpected state {0}", radioState.ToString());
            }
        }

        private int HeaderLengthInAir()
        {
            return (int)Math.Ceiling((s0Length.Value * 8 + lengthFieldLength.Value + s1Length.Value) / 8.0);
        }

        private int HeaderLengthInRAM()
        {
            int ret = (int)s0Length.Value + (int)Math.Ceiling(lengthFieldLength.Value / 8.0) + (int)Math.Ceiling(s1Length.Value / 8.0);
            if(s1Length.Value == 0 && s1Include.Value)
            {
                ret += 1;
            }
            return ret;
        }

        private void SendPacket()
        {
            var dataAddress = (uint)packetPointer.Value;
            var headerLengthInAir = HeaderLengthInAir();
            var addressLength = (int)baseAddressLength.Value + 1;
            var headerLengthInRAM = HeaderLengthInRAM();
            if(headerLengthInAir != headerLengthInRAM)
            {
                this.Log(LogLevel.Noisy, "Header length difference between onAir={0} and inRam={1}", headerLengthInAir, headerLengthInRAM);
            }

            var data = new byte[addressLength + headerLengthInRAM + (uint)maxPacketLength.Value];
            FillCurrentAddress(data, 0, (uint)txAddress.Value);

            sysbus.ReadBytes(dataAddress, headerLengthInRAM, data, addressLength);
            this.Log(LogLevel.Noisy, "Header: {0} S0 {1} Length {2} S1 {3} s1inc {4}", Misc.PrettyPrintCollectionHex(data), s0Length.Value, lengthFieldLength.Value, s1Length.Value, s1Include.Value);
            var payloadLength = data[addressLength + (uint)s0Length.Value];
            if(payloadLength > maxPacketLength.Value)
            {
                this.Log(LogLevel.Error, "Payload length ({0}) longer than the max packet length ({1}), trimming...", payloadLength, maxPacketLength.Value);
                payloadLength = (byte)maxPacketLength.Value;
            }
            sysbus.ReadBytes((ulong)(dataAddress + headerLengthInRAM), payloadLength, data, addressLength + headerLengthInAir);
            this.Log(LogLevel.Noisy, "Data: {0} Maxlen {1} statlen {2}", Misc.PrettyPrintCollectionHex(data), maxPacketLength.Value, staticLength.Value);

            // Suppress BLE LL feature-exchange procedures in simulation.
            // Both SDC instances race to initiate feature exchange
            // (LL_FEATURE_REQ from central, LL_PERIPHERAL_FEATURE_REQ
            // from peripheral) but the ISR never processes the response
            // before the next TX is committed, creating an infinite loop.
            // Convert these control PDUs to Empty PDUs so the LL state
            // machine skips feature exchange entirely.  Feature exchange
            // is optional per BLE spec — the connection proceeds without it.
            if(Channel >= 0 && Channel <= 36 && payloadLength > 0)
            {
                var s0 = data[addressLength];
                var llid = s0 & 0x3;
                if(llid == 3) // LL Control PDU
                {
                    var opcodeIndex = addressLength + headerLengthInAir;
                    var opcode = data[opcodeIndex];
                    if(opcode == 0x08 || opcode == 0x0E) // LL_FEATURE_REQ or LL_PERIPHERAL_FEATURE_REQ
                    {
                        // Convert to Empty PDU: LLID=1, length=0
                        data[addressLength] = (byte)((s0 & 0xFC) | 0x01); // preserve SN/NESN/MD, set LLID=1
                        data[addressLength + (uint)s0Length.Value] = 0;    // length = 0
                        payloadLength = 0;
                    }
                }
            }

            var crcLen = 4;
            pendingTxData = data;
            ScheduleRadioEvents((uint)(headerLengthInAir + payloadLength + crcLen));
        }

        private void ScheduleRadioEvents(uint packetLen)
        {
            var timeSource = machine.LocalTimeSource;
            var now = timeSource.ElapsedVirtualTime;

            // @note  Transmit times assume 1M PHY. Low level BLE firmware
            //        usually takes into account the active phy when calculating
            //        timing delays, so we might need to do that.

            // Bit-counter
            var bcMatchTime = now + TimeInterval.FromMicroseconds(bitCountCompare.Value);
            var bcMatchTimeStamp = new TimeStamp(bcMatchTime, timeSource.Domain);

            // End event
            var endTime = now + TimeInterval.FromMicroseconds((uint)(packetLen) * 8);
            var endTimeStamp = new TimeStamp(endTime, timeSource.Domain);

            // Bypass PPI for Timer0 — PPI's ExecuteInNearestSyncedState
            // introduces multi-quantum delays that break BLE stack timing.
            // On real hardware PPI triggers within one PCLK16M cycle (~62.5ns).
            Timer0?.WriteDoubleWord(0x000, 0x1);  // TASKS_START (idempotent)

            // Address modelled as happening immediately and serves as anchor
            // point for other events.
            SetEvent(Events.Address);

            Timer0?.WriteDoubleWord(0x044, 0x1);  // TASKS_CAPTURE[1]

            // RSSI sample period is 0.25 us. Acceptable to model as happening
            // immediatley upon start
            if(shorts.AddressRSSIStart.Value)
            {
                SetEvent(Events.RSSIEnd);
            }

            // Schedule a single bit-counter compare event not eariler than
            // `bitCountCompare` microseconds from now.
            // This is sufficient for BLE with RIOT stack, however it is possible to use bit
            // counter to generate successive events, which this model will not
            // support.
            if(shorts.AddressBitCountStart.Value)
            {
                timeSource.ExecuteInSyncedState(_ =>
                {
                    SetEvent(Events.BitCountMatch);
                }, bcMatchTimeStamp);
            }

            packetInProgress = true;
            disablePending = false;

            // All end-of-packet processing in a single callback at the end of
            // the on-air time.  Order matches real hardware:
            //   1. End/PHYEnd events fire
            //   2. Shorts execute with zero latency (hardware wiring)
            //   3. FrameSent delivers the packet to the BLE medium AFTER the
            //      sender's state machine has advanced (e.g. Disable → RxEnable)
            //      so that the receiver sees the packet when the sender is
            //      already in its post-TX state.
            timeSource.ExecuteInSyncedState(_ =>
            {
                packetInProgress = false;
                SetEvent(Events.Payload);
                SetEvent(Events.End);
                SetEvent(Events.PHYEnd);
                SetEvent(Events.CRCOk);
                Timer0?.WriteDoubleWord(0x048, 0x1);  // TASKS_CAPTURE[2]

                // FrameSent fires first so the BLE medium can deliver
                // the packet while the sender is still on the TX channel.
                // Shorts execute after, transitioning the sender to its
                // post-TX state (typically Disable → RxEnable → RxIdle).
                if(pendingTxData != null)
                {
                    var txData = pendingTxData;
                    pendingTxData = null;
                    FrameSent?.Invoke(this, txData);
                }

                if(disablePending)
                {
                    disablePending = false;
                    Disable();
                }
                else if(shorts.EndDisable.Value || shorts.PHYEndDisable.Value)
                {
                    Disable();
                }
                else if(shorts.EndStart.Value || shorts.PHYEndStart.Value)
                {
                    Start();
                }
            }, endTimeStamp);
        }

        private void FillCurrentAddress(byte[] data, int startIndex, uint logicalAddress)
        {
            // based on 6.20.2 Address configuration
            var baseAddress = (uint)(logicalAddress == 0 ? baseAddress0.Value : baseAddress1.Value);
            var baseBytes = BitConverter.GetBytes(baseAddress);
            var i = 0;
            if(baseAddressLength.Value > 4)
            {
                this.Log(LogLevel.Error, "Trying to fill the current address, but the base address length is too large ({0}). Limiting to 4.", baseAddressLength.Value);
                baseAddressLength.Value = 4;
            }
            for(var j = 4 - baseAddressLength.Value; j < 4; i++, j++) // we're not supporting BALEN > 4. I don't know how  should it work.
            {
                data[startIndex + i] = baseBytes[j];
            }
            data[startIndex + i] = addressPrefixes[logicalAddress];
        }

        private void ScheduleBufferRetry()
        {
            if(rxBuffer.IsEmpty)
            {
                return;
            }
            var ts = machine.LocalTimeSource;
            var retryStamp = new TimeStamp(
                ts.ElapsedVirtualTime + TimeInterval.FromMicroseconds(5),
                ts.Domain);
            ts.ExecuteInSyncedState(_ =>
            {
                if(radioState == State.RxIdle && !rxBuffer.IsEmpty)
                {
                    Start();
                }
                else if(!rxBuffer.IsEmpty)
                {
                    // Radio still not ready — retry again
                    ScheduleBufferRetry();
                }
            }, retryStamp);
        }

        private State radioState;
        private bool packetInProgress;
        private bool disablePending;
        private byte[] pendingTxData;
        private byte[] addressPrefixes;
        private Shorts shorts;
        private IFlagRegisterField crcIncludedInLength;
        private IValueRegisterField staticLength;
        private IValueRegisterField baseAddressLength;
        private IValueRegisterField baseAddress0;
        private IValueRegisterField baseAddress1;
        private IValueRegisterField txAddress;
        private IFlagRegisterField[] rxAddressEnabled;

        private IValueRegisterField crcLength;
        private IValueRegisterField maxPacketLength;
        private IEnumRegisterField<CRCAddressHandling> crcSkipAddress;
        private IValueRegisterField crcPolynomial;
        private IValueRegisterField crcInitialValue;
        private IEnumRegisterField<CCAMode> ccaMode;

        private IValueRegisterField bitCountCompare;
        private IValueRegisterField termLength;
        private IValueRegisterField codeIndicatorLength;
        private IFlagRegisterField powerOn;
        private IFlagRegisterField s1Include;
        private IValueRegisterField s1Length;
        private IValueRegisterField s0Length;
        private IValueRegisterField lengthFieldLength;
        private IFlagRegisterField frequencyMap;
        private IValueRegisterField frequency;
        private IValueRegisterField packetPointer;

        private NRF52840_Timer timer0;

        private NRF52840_Timer Timer0
        {
            get
            {
                if(timer0 == null)
                {
                    machine.TryGetByName("sysbus.timer0", out timer0);
                }
                return timer0;
            }
        }

        private readonly IFlagRegisterField[] events;
        private readonly InterruptManager<Events> interruptManager;

        private readonly ConcurrentQueue<(byte[] Frame, IRadio Sender, int SenderChannel)> rxBuffer;

        private readonly Dictionary<uint, int> bluetoothLEChannelMap = new Dictionary<uint, int>()
        {
           { 2402, 37 },
           { 2404, 0 },
           { 2406, 1 },
           { 2408, 2 },
           { 2410, 3 },
           { 2412, 4 },
           { 2414, 5 },
           { 2416, 6 },
           { 2418, 7 },
           { 2420, 8 },
           { 2422, 9 },
           { 2424, 10 },
           { 2426, 38 },
           { 2428, 11 },
           { 2430, 12 },
           { 2432, 13 },
           { 2434, 14 },
           { 2436, 15 },
           { 2438, 16 },
           { 2440, 17 },
           { 2442, 18 },
           { 2444, 19 },
           { 2446, 20 },
           { 2448, 21 },
           { 2450, 22 },
           { 2452, 23 },
           { 2454, 24 },
           { 2456, 25 },
           { 2458, 26 },
           { 2460, 27 },
           { 2462, 28 },
           { 2464, 29 },
           { 2466, 30 },
           { 2468, 31 },
           { 2470, 32 },
           { 2472, 33 },
           { 2474, 34 },
           { 2476, 35 },
           { 2478, 36 },
           { 2480, 39 },
        };

        private const int DefaultRSSISample = 10;
        private const int MaxRxBufferSize = 64;

        private struct Shorts
        {
            public IFlagRegisterField ReadyStart;
            public IFlagRegisterField EndDisable;
            public IFlagRegisterField DisabledTxEnable;
            public IFlagRegisterField DisabledRxEnable;
            public IFlagRegisterField AddressRSSIStart;
            public IFlagRegisterField EndStart;
            public IFlagRegisterField AddressBitCountStart;
            public IFlagRegisterField DisabledRSSIStop;
            public IFlagRegisterField RxReadyCCAStart;
            public IFlagRegisterField CCAIdleTxEnable;
            public IFlagRegisterField CCABusyDisable;
            public IFlagRegisterField FrameStartBitCountStart;
            public IFlagRegisterField ReadyEnergyDetectStart;
            public IFlagRegisterField EnergyDetectEndDisable;
            public IFlagRegisterField CCAIdleStop;
            public IFlagRegisterField TxReadyStart;
            public IFlagRegisterField RxReadyStart;
            public IFlagRegisterField PHYEndDisable;
            public IFlagRegisterField PHYEndStart;
        }

        private enum CRCAddressHandling
        {
            Include = 0,
            Skip = 1,
            IEEE802154 = 2
        }

        private enum State
        {
            Disabled = 0,
            RxRampup = 1,
            RxIdle = 2,
            Rx = 3,
            RxDisable = 4,
            TxRampup = 9,
            TxIdle = 10,
            Tx = 11,
            TxDisable = 12,
        }

        private enum Events
        {
            Ready = 0,
            Address = 1,
            Payload = 2,
            End = 3,
            Disabled = 4,
            DeviceAddressMatch = 5,
            DeviceAddressMiss = 6,
            RSSIEnd = 7,
            Reserved8 = 8,
            Reserved9 = 9,
            BitCountMatch = 10,
            Reserved11 = 11,
            CRCOk = 12,
            CRCError = 13,
            FrameStart = 14,
            EnergyDetectEnd = 15,
            EnergyDetectStopped = 16,
            CCAIdle = 17,
            CCABusy = 18,
            CCAStopped = 19,
            RateBoost = 20,
            TxReady = 21,
            RxReady = 22,
            MACHeaderMatch = 23,
            Reserved24 = 24,
            Reserved25 = 25,
            Sync = 26,
            PHYEnd = 27,
            Reserved28 = 28,
            Reserved29 = 29,
            Reserved30 = 30,
            Reserved31 = 31
        }

        private enum CCAMode
        {
            EdMode,
            CarrierMode,
            CarrierAndEdMode,
            CarrierOrEdMode,
            EdMoteTest1
        }

        private enum Registers
        {
            TxEnable = 0x000,
            RxEnable = 0x004,
            Start = 0x008,
            Stop = 0x00C,
            Disable = 0x010,
            RSSIStart = 0x014,
            RSSIStop = 0x018,
            BitCounterStart = 0x01C,
            BitCounterStop = 0x020,
            EnergyDetectStart = 0x024,
            EnergyDetectStop = 0x028,
            CCAStart = 0x02C,
            CCAStop = 0x030,
            Ready = 0x100,
            AddressSentOrReceived = 0x104,
            PayloadSentOrReceived = 0x108,
            PacketSentOrReceived = 0x10C,
            RadioDisabled = 0x110,
            DeviceMatch = 0x114,
            DeviceMiss = 0x118,
            RSSIEnd = 0x11C,
            BitCounterMatch = 0x128,
            CRCOk = 0x130,
            CRCError = 0x134,
            FrameStartReceived = 0x138,
            EnergyDetectEnd = 0x13C,
            EnergyDetectStopped = 0x140,
            CCAIdle = 0x144,
            CCABusy = 0x148,
            CCAStopped = 0x14C,
            RateBoost = 0x150,
            TxReady = 0x154,
            RxReady = 0x158,
            MACHeaderMatch = 0x15C,
            Sync = 0x168,
            PHYEnd = 0x16C,
            Shorts = 0x200,
            InterruptEnable = 0x304,
            InterruptDisable = 0x308,
            CRCStatus = 0x400,
            RxMatch = 0x408,
            RxCRC = 0x40C,
            DeviceAddressMatchIndex = 0x410,
            PayloadStatus = 0x414,
            PDUStatus = 0x450,
            PacketPointer = 0x504,
            Frequency = 0x508,
            TxPower = 0x50C,
            Mode = 0x510,
            PacketConfiguration0 = 0x514,
            PacketConfiguration1 = 0x518,
            BaseAddress0 = 0x51C,
            BaseAddress1 = 0x520,
            Prefix0 = 0x524,
            Prefix1 = 0x528,
            TxAddress = 0x52C,
            RxAddresses = 0x530,
            CRCConfiguration = 0x534,
            CRCPolynomial = 0x538,
            CRCInitialValue = 0x53C,
            InterframeSpacing = 0x544,
            RSSISample = 0x548,
            State = 0x550,
            DataWhiteningInitialValue = 0x554,
            BitCounterCompare = 0x560,
            DFEGPIOSelect0 = 0x568,
            DFEGPIOSelect1 = 0x58C,
            DeviceAddressBaseSegment0 = 0x600,
            DeviceAddressBaseSegment1 = 0x604,
            DeviceAddressBaseSegment2 = 0x608,
            DeviceAddressBaseSegment3 = 0x60C,
            DeviceAddressBaseSegment4 = 0x610,
            DeviceAddressBaseSegment5 = 0x614,
            DeviceAddressBaseSegment6 = 0x618,
            DeviceAddressBaseSegment7 = 0x61C,
            DeviceAddressPrefix0 = 0x620,
            DeviceAddressPrefix1 = 0x624,
            DeviceAddressPrefix2 = 0x628,
            DeviceAddressPrefix3 = 0x62C,
            DeviceAddressPrefix4 = 0x630,
            DeviceAddressPrefix5 = 0x634,
            DeviceAddressPrefix6 = 0x638,
            DeviceAddressPrefix7 = 0x63C,
            DeviceAddressMatchConfiguration = 0x640,
            SearchPatternConfiguration = 0x644,
            PatternMask = 0x648,
            ModeConfiguration0 = 0x650,
            StartOfFrameDelimiter = 0x660,
            EnergyDetectLoopCount = 0x664,
            EnergyDetectLevel = 0x668,
            CCAControl = 0x66C,
            CTEInlineConfig = 0x73C,
            DFEControl1 = 0x740,
            DFEControl2 = 0x74C,
            DFESwitch = 0x774,
            SwitchPattern = 0x778,
            ClearPattern = 0x77C,
            DFEMode = 0x900,
            PowerControl = 0xFFC
        }
    }
}
