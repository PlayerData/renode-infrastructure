//
// Copyright (c) 2010-2026 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.Wireless;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class NRF52840_RadioTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine);

            sysbus = (SystemBus)machine.SystemBus;
            memory = new MappedMemory(machine, MemorySize);
            sysbus.Register(memory, new BusPointRegistration(PacketBufferAddress));

            radio = new NRF52840_Radio(machine);
            sysbus.Register(radio, new BusPointRegistration(RadioBaseAddress));
        }

        [TearDown]
        public void TearDown()
        {
            EmulationManager.Instance.CurrentEmulation.RemoveMachine(machine);
        }

        [Test]
        public void ShouldWriteHeaderDirectlyWhenS1InclNotSet()
        {
            ConfigurePacketFormat(s0Len: 1, lengthFieldBits: 8, s1LenBits: 0, s1Include: false, balen: 3, maxLen: 255);
            SetPacketPointer(PacketBufferAddress);
            EnableRx();

            byte payloadLen = 3;
            var frame = BuildFrame(
                address: new byte[] { 0xAA, 0xBB, 0xCC, 0xDD },
                s0: 0x02,
                length: payloadLen,
                payload: new byte[] { 0xCA, 0xFE, 0x42 },
                crc: new byte[] { 0x00, 0x00, 0x00, 0x00 }
            );

            radio.ReceiveFrame(frame, null);

            var ram = sysbus.ReadBytes(PacketBufferAddress, 2 + payloadLen);

            Assert.AreEqual(0x02, ram[0], "S0 byte should match frame");
            Assert.AreEqual(payloadLen, ram[1], "Length byte should match frame");
            Assert.AreEqual(0xCA, ram[2], "Payload byte 0 immediately after header");
            Assert.AreEqual(0xFE, ram[3], "Payload byte 1");
            Assert.AreEqual(0x42, ram[4], "Payload byte 2");
        }

        [Test]
        public void ShouldEndReceptionAfterHalfTheAirtimeOn2MbitPhy()
        {
            PrepareReceptionOfTwentyByteBlePayload(BleMode2Mbit);

            radio.ReceiveFrame(BuildTwentyBytePayloadFrame(), null);
            RunForMicroseconds(150);

            Assert.AreEqual(1, radio.ReadDoubleWord(EventsEnd), "26 bytes take 104us at 2Mbit, so END must have fired by 150us");
        }

        [Test]
        public void ShouldNotEndReceptionBeforeFullAirtimeOn1MbitPhy()
        {
            PrepareReceptionOfTwentyByteBlePayload(BleMode1Mbit);

            radio.ReceiveFrame(BuildTwentyBytePayloadFrame(), null);
            RunForMicroseconds(150);

            Assert.AreEqual(0, radio.ReadDoubleWord(EventsEnd), "26 bytes take 208us at 1Mbit");
        }

        [Test]
        public void ShouldApplyEndDisableShortConfiguredWhenEndFires()
        {
            PrepareReceptionOfTwentyByteBlePayload(BleMode1Mbit);
            radio.WriteDoubleWord(Shorts, ShortEndDisable);

            radio.ReceiveFrame(BuildTwentyBytePayloadFrame(), null);
            RunForMicroseconds(210);
            radio.WriteDoubleWord(Shorts, 0);
            RunForMicroseconds(30);

            Assert.AreEqual(1, radio.ReadDoubleWord(EventsEnd));
            Assert.AreEqual(1, radio.ReadDoubleWord(EventsDisabled), "END_DISABLE was set when END fired, so the radio must disable");
        }

        private void PrepareReceptionOfTwentyByteBlePayload(uint mode)
        {
            EmulationManager.Instance.CurrentEmulation.MasterTimeSource.Quantum = TimeInterval.FromMicroseconds(1);
            ConfigurePacketFormat(s0Len: 1, lengthFieldBits: 8, s1LenBits: 0, s1Include: false, balen: 3, maxLen: 255);
            SetPacketPointer(PacketBufferAddress);
            radio.WriteDoubleWord(Mode, mode);
            EnableRx();
        }

        private static byte[] BuildTwentyBytePayloadFrame()
        {
            return BuildFrame(
                address: new byte[] { 0xAA, 0xBB, 0xCC, 0xDD },
                s0: 0x01,
                length: 20,
                payload: new byte[20],
                crc: new byte[] { 0x00, 0x00, 0x00, 0x00 }
            );
        }

        private static void RunForMicroseconds(ulong microseconds)
        {
            EmulationManager.Instance.CurrentEmulation.RunFor(TimeInterval.FromMicroseconds(microseconds));
        }

        private void ConfigurePacketFormat(uint s0Len, uint lengthFieldBits, uint s1LenBits, bool s1Include, uint balen, uint maxLen)
        {
            uint pcnf0 = (lengthFieldBits & 0xF)
                | ((s0Len & 0x1) << 8)
                | ((s1LenBits & 0xF) << 16)
                | ((s1Include ? 1u : 0u) << 20);
            radio.WriteDoubleWord(PacketConfiguration0, pcnf0);

            uint pcnf1 = (maxLen & 0xFF) | ((balen & 0x7) << 16);
            radio.WriteDoubleWord(PacketConfiguration1, pcnf1);
        }

        private void SetPacketPointer(uint address)
        {
            radio.WriteDoubleWord(PacketPointer, address);
        }

        private void EnableRx()
        {
            radio.WriteDoubleWord(RxEnable, 1);
        }

        private static byte[] BuildFrame(byte[] address, byte s0, byte length, byte[] payload, byte[] crc)
        {
            var frame = new byte[address.Length + 2 + payload.Length + crc.Length];
            address.CopyTo(frame, 0);
            frame[address.Length] = s0;
            frame[address.Length + 1] = length;
            payload.CopyTo(frame, address.Length + 2);
            crc.CopyTo(frame, address.Length + 2 + payload.Length);
            return frame;
        }

        private Machine machine;
        private SystemBus sysbus;
        private MappedMemory memory;
        private NRF52840_Radio radio;

        private const long RxEnable = 0x004;
        private const long EventsEnd = 0x10C;
        private const long EventsDisabled = 0x110;
        private const long Shorts = 0x200;
        private const long Mode = 0x510;
        private const uint ShortEndDisable = 0x2;
        private const uint BleMode1Mbit = 3;
        private const uint BleMode2Mbit = 4;
        private const long PacketPointer = 0x504;
        private const long PacketConfiguration0 = 0x514;
        private const long PacketConfiguration1 = 0x518;

        private const uint RadioBaseAddress = 0x40001000;
        private const uint PacketBufferAddress = 0x20000000;
        private const int MemorySize = 0x1000;
    }
}
