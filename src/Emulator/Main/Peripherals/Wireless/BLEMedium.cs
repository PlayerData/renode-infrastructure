//
// Copyright (c) 2010-2022 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Core;

namespace Antmicro.Renode.Peripherals.Wireless
{
    public static class BLEExtensions
    {
        public static void CreateBLEMedium(this Emulation emulation, string name)
        {
            emulation.ExternalsManager.AddExternal(new BLEMedium(), name);
        }
    }

    public sealed class BLEMedium : WirelessMedium
    {
        public BLEMedium() { }

        // In real BLE the radio receives a packet within the same
        // connection event it was transmitted — propagation delay is
        // effectively zero.  The default WirelessMedium defers delivery
        // via HandleTimeDomainEvent, which can push the reception into a
        // later virtual-time quantum and therefore a later connection
        // event.  Synchronous delivery matches real hardware behaviour.
        // Deferred delivery via HandleTimeDomainEvent allows both CPUs
        // to process ISRs and prepare responses between TX and RX.
        // Synchronous delivery prevents the receiver's CPU from running
        // its ISR before the sender expects a response.
        protected override bool SynchronousDelivery => false;

        // Relax channel matching to compensate for simulation timing
        // drift.  In real BLE both radios are tuned to the same channel
        // during a connection event, but in simulation the receiver may
        // have hopped by the time FrameSent fires.
        //
        // Deliver when both radios are in the same channel group:
        //  - Advertising channels (37-39): any advertising → any advertising
        //  - Data channels (0-36): any data → any data
        //
        // The NRF52840_Radio rx-buffer drain applies exact data-channel
        // matching, so wrong-channel data frames are discarded there.
        protected override bool ChannelMatch(IRadio sender, IRadio receiver)
        {
            var sCh = sender.Channel;
            var rCh = receiver.Channel;
            if(sCh == rCh)
            {
                return true;
            }
            // Same channel group?
            bool sAdv = sCh >= 37 && sCh <= 39;
            bool rAdv = rCh >= 37 && rCh <= 39;
            bool sData = sCh >= 0 && sCh <= 36;
            bool rData = rCh >= 0 && rCh <= 36;
            return (sAdv && rAdv) || (sData && rData);
        }
    }
}