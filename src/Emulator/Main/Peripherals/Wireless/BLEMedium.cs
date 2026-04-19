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

        // In real BLE, a packet transmitted on channel N is received by any
        // radio tuned to channel N during the transmission.  Renode checks
        // the receiver's channel at FrameSent time (end of TX), but by then
        // the receiver may have hopped—especially during the tight T_IFS
        // (150 µs) window between ADV_IND and SCAN_REQ/CONNECT_IND.
        //
        // BLE advertising cycles through channels 37, 38, 39 within a few
        // hundred microseconds.  Rather than requiring exact channel match
        // (which fails due to simulation timing drift), we allow delivery
        // when both sender and receiver are on advertising channels.
        // For data channels (0-36) we keep strict matching.
        protected override bool ChannelMatch(IRadio sender, IRadio receiver)
        {
            var sCh = sender.Channel;
            var rCh = receiver.Channel;

            if(sCh == rCh)
            {
                return true;
            }

            // Both on advertising channels — allow cross-channel delivery
            if(IsAdvertisingChannel(sCh) && IsAdvertisingChannel(rCh))
            {
                return true;
            }

            return false;
        }

        private static bool IsAdvertisingChannel(int channel)
        {
            return channel == 37 || channel == 38 || channel == 39;
        }
    }
}