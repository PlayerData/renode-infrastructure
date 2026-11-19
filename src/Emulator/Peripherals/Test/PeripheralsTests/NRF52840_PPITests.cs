//
// Copyright (c) 2010-2026 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class NRF52840_PPITests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine);
            sysbus = (SystemBus)machine.SystemBus;

            eventSource = new FakeEventSource();
            sysbus.Register(eventSource, new BusRangeRegistration(EventSourceBase, 0x1000));

            taskMemory = new MappedMemory(machine, 0x1000);
            sysbus.Register(taskMemory, new BusPointRegistration(TaskMemoryBase));

            ppi = new NRF52840_PPI(machine);
            sysbus.Register(ppi, new BusRangeRegistration(PpiBase, 0x1000));
        }

        [TearDown]
        public void TearDown()
        {
            EmulationManager.Instance.CurrentEmulation.RemoveMachine(machine);
        }

        [Test]
        public void ShouldTriggerTaskConfiguredWhenEventArrivedEvenIfChannelIsRewiredBeforeSync()
        {
            ppi.WriteDoubleWord(Channel0EventEndpoint, EventSourceBase + EventOffset);
            ppi.WriteDoubleWord(Channel0TaskEndpoint, (uint)(TaskMemoryBase + OriginalTaskOffset));
            ppi.WriteDoubleWord(ChannelEnableSet, 1);

            eventSource.Fire(EventOffset);
            ppi.WriteDoubleWord(Channel0TaskEndpoint, (uint)(TaskMemoryBase + NewTaskOffset));

            EmulationManager.Instance.CurrentEmulation.RunFor(TimeInterval.FromMicroseconds(100));

            Assert.AreEqual(1, taskMemory.ReadDoubleWord(OriginalTaskOffset), "task configured at event time should be triggered");
            Assert.AreEqual(0, taskMemory.ReadDoubleWord(NewTaskOffset), "task configured after the event must not be triggered");
        }

        private IMachine machine;
        private SystemBus sysbus;
        private NRF52840_PPI ppi;
        private FakeEventSource eventSource;
        private MappedMemory taskMemory;

        private const ulong PpiBase = 0x4001F000;
        private const uint EventSourceBase = 0x40010000;
        private const uint TaskMemoryBase = 0x40020000;
        private const uint EventOffset = 0x100;
        private const long OriginalTaskOffset = 0x0;
        private const long NewTaskOffset = 0x4;
        private const long ChannelEnableSet = 0x504;
        private const long Channel0EventEndpoint = 0x510;
        private const long Channel0TaskEndpoint = 0x514;

        private class FakeEventSource : IDoubleWordPeripheral, INRFEventProvider
        {
            public void Fire(uint offset)
            {
                EventTriggered?.Invoke(offset);
            }

            public uint ReadDoubleWord(long offset)
            {
                return 0;
            }

            public void WriteDoubleWord(long offset, uint value)
            {
            }

            public void Reset()
            {
            }

            public event Action<uint> EventTriggered;
        }
    }
}
