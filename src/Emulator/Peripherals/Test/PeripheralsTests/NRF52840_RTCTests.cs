//
// Copyright (c) 2010-2026 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class NRF52840_RTCTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            rtc = new NRF52840_RTC(machine, 4);
            routedEvents = new List<uint>();
            rtc.EventTriggered += offset => routedEvents.Add(offset);
        }

        [Test]
        public void ShouldRouteTickToPpiWhenTickEventEnabledBeforeStart()
        {
            rtc.WriteDoubleWord(EventEnableSet, TickBit);
            rtc.WriteDoubleWord(TasksStart, 1);

            AdvanceByMicroseconds(100);

            Assert.Contains(EventsTick, routedEvents);
        }

        [Test]
        public void ShouldRouteTickToPpiWhenTickEventEnabledAfterStart()
        {
            rtc.WriteDoubleWord(TasksStart, 1);
            rtc.WriteDoubleWord(EventEnableSet, TickBit);

            AdvanceByMicroseconds(100);

            Assert.Contains(EventsTick, routedEvents);
        }

        [Test]
        public void ShouldNotRouteTickToPpiWhenOnlyTickInterruptEnabled()
        {
            rtc.WriteDoubleWord(InterruptEnableSet, TickBit);
            rtc.WriteDoubleWord(TasksStart, 1);

            AdvanceByMicroseconds(100);

            Assert.IsFalse(routedEvents.Contains(EventsTick));
            Assert.AreEqual(1, rtc.ReadDoubleWord(EventsTick));
        }

        [Test]
        public void ShouldRaiseTickWhenCounterIncrementsEvenIfTickEnabledMidTick()
        {
            rtc.WriteDoubleWord(TasksStart, 1);
            AdvanceByMicroseconds(45);

            rtc.WriteDoubleWord(EventEnableSet, TickBit);
            AdvanceByMicroseconds(17);

            Assert.AreEqual(2, rtc.ReadDoubleWord(Counter));
            Assert.Contains(EventsTick, routedEvents, "TICK must coincide with the COUNTER increment at 61us");
        }

        private void AdvanceByMicroseconds(ulong microseconds)
        {
            ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(microseconds));
        }

        private IMachine machine;
        private NRF52840_RTC rtc;
        private List<uint> routedEvents;

        private const long TasksStart = 0x000;
        private const uint EventsTick = 0x100;
        private const long InterruptEnableSet = 0x304;
        private const long EventEnableSet = 0x344;
        private const uint TickBit = 0x1;
        private const long Counter = 0x504;
    }
}
