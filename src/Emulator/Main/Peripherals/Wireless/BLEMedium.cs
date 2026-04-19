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
        protected override bool SynchronousDelivery => true;

        // Allow delivery when both radios are on advertising channels
        // (37-39), since simulation timing drift causes cross-channel
        // mismatches within the advertising cycle.  Data channels
        // require exact match.
        protected override bool ChannelMatch(IRadio sender, IRadio receiver)
        {
            var sCh = sender.Channel;
            var rCh = receiver.Channel;
            if(sCh == rCh)
            {
                return true;
            }
            return IsAdvertisingChannel(sCh) && IsAdvertisingChannel(rCh);
        }

        private static bool IsAdvertisingChannel(int ch)
        {
            return ch == 37 || ch == 38 || ch == 39;
        }
    }
}