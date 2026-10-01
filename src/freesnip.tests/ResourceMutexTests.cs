using System;
using System.Threading;
using System.Threading.Tasks;
using freesnip.helpers;
using Xunit;

namespace freesnip.tests
{
    public class ResourceMutexTests
    {
        [Fact]
        public void AuthoritativeName_MatchesSingleInstanceArbitratorSpecification()
        {
            Assert.Equal(@"Global\FreeSnip_SingleInstance_Arbitrator", ResourceMutex.SingleInstanceArbitratorName);
            Assert.Equal(@"Global\SnapVox_SingleInstance_Arbitrator", ResourceMutex.LegacySingleInstanceArbitratorName);
        }

        [Fact]
        public void CrossThread_AcquisitionOnThreadA_DisposalOnThreadB_CompletesWithoutApplicationException()
        {
            string arbitratorId = "Test_CrossThread_" + Guid.NewGuid().ToString("N");
            ResourceMutex? arbitrator = null;
            int threadAId = -1;
            int threadBId = -1;

            var threadA = new Thread(() =>
            {
                threadAId = Environment.CurrentManagedThreadId;
                arbitrator = ResourceMutex.Create(arbitratorId);
            });
            threadA.Start();
            threadA.Join();

            Assert.NotNull(arbitrator);
            Assert.True(arbitrator.IsLocked, "Thread A should have acquired the lock.");

            Exception? disposeException = null;
            var threadB = new Thread(() =>
            {
                threadBId = Environment.CurrentManagedThreadId;
                try
                {
                    arbitrator.Dispose();
                }
                catch (Exception ex)
                {
                    disposeException = ex;
                }
            });
            threadB.Start();
            threadB.Join();

            Assert.NotEqual(threadAId, threadBId);
            Assert.Null(disposeException);
            Assert.False(arbitrator.IsLocked, "Lock should be released after disposal.");

            using var arbitratorNext = ResourceMutex.Create(arbitratorId);
            Assert.True(arbitratorNext.IsLocked, "Subsequent instance must be able to acquire after cross-thread disposal.");
        }

        [Fact]
        public void RelaunchStressTest_20RapidStartStopCycles_LeavesZeroAbandonedHandles()
        {
            string arbitratorId = "Test_Stress_" + Guid.NewGuid().ToString("N");

            for (int i = 0; i < 20; i++)
            {
                using var arbitrator = ResourceMutex.Create(arbitratorId);
                Assert.True(arbitrator.IsLocked, $"Arbitrator should acquire on cycle {i + 1}");
            }

            using var finalArbitrator = ResourceMutex.Create(arbitratorId);
            Assert.True(finalArbitrator.IsLocked, "Arbitrator should acquire cleanly after 20 cycles with zero abandoned handles.");
        }

        [Fact]
        public void SecondaryInstance_WhenLocked_FailsAcquisitionAndDoesNotStealPermit()
        {
            string arbitratorId = "Test_Contention_" + Guid.NewGuid().ToString("N");

            using var primary = ResourceMutex.Create(arbitratorId);
            Assert.True(primary.IsLocked, "Primary instance must acquire lock.");

            var secondary = ResourceMutex.Create(arbitratorId);
            Assert.False(secondary.IsLocked, "Secondary instance must fail acquisition.");

            secondary.Dispose();
            Assert.True(primary.IsLocked, "Primary instance must remain locked after secondary disposal.");

            primary.Dispose();

            using var tertiary = ResourceMutex.Create(arbitratorId);
            Assert.True(tertiary.IsLocked, "Tertiary instance must acquire lock after primary disposal.");
        }

        [Fact]
        public void ConcurrentMultiThreadedDisposal_IsIdempotentAndSafe()
        {
            string arbitratorId = "Test_ConcurrentDispose_" + Guid.NewGuid().ToString("N");
            var arbitrator = ResourceMutex.Create(arbitratorId);
            Assert.True(arbitrator.IsLocked);

            Exception? caughtException = null;
            Parallel.For(0, 16, _ =>
            {
                try
                {
                    arbitrator.Dispose();
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref caughtException, ex, null);
                }
            });

            Assert.Null(caughtException);
            Assert.False(arbitrator.IsLocked);

            using var next = ResourceMutex.Create(arbitratorId);
            Assert.True(next.IsLocked, "New arbitrator should acquire successfully after concurrent disposals.");
        }
    }
}

