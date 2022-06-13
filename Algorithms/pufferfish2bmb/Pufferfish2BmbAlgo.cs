using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace dcrpt_miner 
{
    public class Pufferfish2BmbAlgo : IAlgorithm
    {
        public static bool GPU => false;
        public static bool CPU => true;
        public static double DevFee => 0.015d;
        public static string DevWallet => "VFNCREEgY14rLCM2IlJAMUYlYiwrV1FGIlBDNEVQGFsvKlxBUyEzQDBUY1QoKFxHUyZF".AsWalletAddress();
        public string Name => "pufferfish2bmb";

        private List<BlockingCollection<Job>> Workers = new List<BlockingCollection<Job>>();
        private IConfiguration Configuration;
        private RandomNumberGenerator _global = RandomNumberGenerator.Create();
        private bool disposedValue;
        private CancellationTokenSource ThreadSource = new CancellationTokenSource();


        public Pufferfish2BmbAlgo()
        {

        }

        public void Initialize(ILogger logger, Channels channels, ManualResetEvent PauseEvent)
        {
            var configurationBuilder = new ConfigurationBuilder();
            configurationBuilder.AddJsonFile("config.pufferfish2bmb.json");
            configurationBuilder.AddCommandLine(Environment.GetCommandLineArgs());
            Configuration = configurationBuilder.Build().GetSection("pufferfish2bmb");

            var threads = Configuration.GetValue<int>("threads");

            if (threads <= 0) {
                threads = Environment.ProcessorCount;
            }

            StatusManager.CpuHashCount = new ulong[threads];

            for (uint i = 0; i < threads; i++) {
                var queue = new BlockingCollection<Job>();

                var tid = i;
                logger.LogDebug("Starting CpuWorker[{}] thread", tid);
                new Thread(() => {
                    var token = ThreadSource.Token;
                    while (!token.IsCancellationRequested) {
                        var job = queue.Take(token);
                        DoCPUWork(tid, job, channels, PauseEvent);
                    }
                }).UnsafeStart();

                Workers.Add(queue); 
            }
        }

        public void ExecuteJob(Job job)
        {
            Parallel.ForEach(Workers, worker => {
                worker.Add(job);
            });
        }

        private unsafe void DoCPUWork(uint id, Job job, Channels channels, ManualResetEvent pauseEvent)
        {
            byte[] buffer = new byte[4];
            _global.GetBytes(buffer);
            var rand = new Random(BitConverter.ToInt32(buffer, 0));

            Span<byte> concat = new byte[64];
            Span<byte> hash = new byte[119]; // TODO: verify this matches PF_HASHSPACE in all cases
            Span<byte> solution = new byte[32];

            int challengeBytes = job.Difficulty / 8;
            int remainingBits = job.Difficulty - (8 * challengeBytes);

            for (int i = 0; i < 32; i++) concat[i] = job.Nonce[i];
            for (int i = 33; i < 64; i++) concat[i] = (byte)rand.Next(0, 256);
            concat[32] = (byte)job.Difficulty;

            Thread.BeginThreadAffinity();

            using (SHA256 sha256 = SHA256.Create())
            fixed (byte* ptr = concat, hashPtr = hash)
            {
                ulong* locPtr = (ulong*)(ptr + 33);
                uint* hPtr = (uint*)hashPtr;

                uint count = 10;
                while (!job.CancellationToken.IsCancellationRequested)
                {
                    ++*locPtr;

                    Unmanaged.pf_newhash(ptr, 64, 0, 8, hashPtr);
                    var sha256Hash = sha256.ComputeHash(hash.ToArray());

                    if (checkLeadingZeroBits(sha256Hash, job.Difficulty, challengeBytes, remainingBits))
                    {
                        channels.Solutions.Writer.TryWrite(concat.Slice(32).ToArray());
                    }

                    if (count == 0) {
                        StatusManager.CpuHashCount[id] += 10;

                        count = 10;
                        if (id < 2) {
                            // Be nice to other threads and processes
                            Thread.Sleep(1);
                        }

                        pauseEvent.WaitOne();
                    }

                    --count;
                }
            }
        }

        // TODO: Move to util class or something??
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe bool checkLeadingZeroBits(byte[] hash, int challengeSize, int challengeBytes, int remainingBits) {
            for (int i = 0; i < challengeBytes; i++) {
                if (hash[i] != 0) return false;
            }

            if (remainingBits > 0) return hash[challengeBytes]>>(8-remainingBits) == 0;
            else return true;
        }

        unsafe class Unmanaged
        {
            [DllImport("Algorithms/pufferfish2bmb/pufferfish2", ExactSpelling = true)]
            [SuppressGCTransition]
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static extern int pf_newhash(byte* pass, int pass_sz, int cost_t, int cost_m, byte* hash);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects)
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                disposedValue = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~Pufferfish2BmbAlgo()
        // {
        //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
