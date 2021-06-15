using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nethereum.RPC.Web3;
using Nethereum.Web3;
using Newtonsoft.Json;
using static BSC_Public_Node_Health.Program;

namespace BSC_Public_Node_Health
{
    class Program
    {

        public static string configfile = "settings//settings.config";
        public static ConcurrentBag<network> configclass = new ConcurrentBag<network>();

        public class nodehealth
        {
            public string url;
            public node rpc;
            public BigInteger blockheight;
            public bool syncing;
            public string nodeVersion;
            public double responsetime = 99999;
        }


        public class node
        {
            public string url;
            public string name;
            public bool privateNode;

            public node(string _url, string _name, bool _privateNode = false)
            {
                url = _url;
                name = _name;
                privateNode = _privateNode;
            }

        }


        public class network
        {
            public string name;
            public ConcurrentBag<string> rpcs = new ConcurrentBag<string>();
            public ConcurrentBag<node> rpcservers = new ConcurrentBag<node>();
            public ConcurrentBag<nodehealth> healthchecks = new ConcurrentBag<nodehealth>();
            public ConcurrentBag<string> errors = new ConcurrentBag<string>();
            public int blocktime;
        }


        public class blockcheck
        {

            public BigInteger blockNumber;
            public double responsetime;
        }



        static async Task<blockcheck> getBlock(string url)
        {


            var web3 = new Web3(url);


            Stopwatch stopWatch = new Stopwatch();
            stopWatch.Start();
           
            BigInteger latestBlockNumber = await web3.Eth.Blocks.GetBlockNumber.SendRequestAsync();

            stopWatch.Stop();

            TimeSpan ts = stopWatch.Elapsed;


            blockcheck checkresult = new blockcheck();
            checkresult.blockNumber = latestBlockNumber;
            checkresult.responsetime = ts.TotalMilliseconds;

            return checkresult;


        }


        public static string ToApplicationPath(string fileName)
        {

            var path = Directory.GetCurrentDirectory();
            return Path.Combine(path, fileName);
        }
        public static void readAppStatus()
        {

           


            if (!System.IO.File.Exists(ToApplicationPath(configfile)))
            {
                saveAppStatus();
            }


            string jsonString = File.ReadAllText(ToApplicationPath(configfile));
            configclass = JsonConvert.DeserializeObject<ConcurrentBag<network>>(jsonString);
        }

        public static void saveAppStatus()
        {
            try
            {
                string jsonString = JsonConvert.SerializeObject(configclass);
                File.WriteAllText(ToApplicationPath(configfile), jsonString);
            }
            catch (Exception ex)
            {

            }


        }


        static void Main(string[] args)
        {

            readAppStatus();

            while (true)
            {

                Console.Clear();
                Console.WriteLine($"Check at {DateTime.UtcNow.ToString()}\n");

                foreach (network n in configclass)
                {


                    Console.WriteLine($"{n.name}\n");

                    n.healthchecks.Clear();
                    n.errors.Clear();


                    int maxConcurrency = 25;
                    using (SemaphoreSlim concurrencySemaphore = new SemaphoreSlim(maxConcurrency))
                    {
                        List<Task> tasks = new List<Task>();
                        foreach (node rpc in n.rpcservers)
                        {

                            concurrencySemaphore.Wait();
                            var t = Task.Factory.StartNew(() =>
                            {

                                try
                                {


                                    var web3 = new Web3(rpc.url);

                                   
                                    blockcheck checkresult = getBlock(rpc.url).Result;

                                    BigInteger latestBlockNumber = checkresult.blockNumber;

                                    bool syncing = web3.Eth.Syncing.SendRequestAsync().Result.IsSyncing;

                                    Web3ClientVersion w3cv = new Web3ClientVersion(web3.Client);
                                    string version = w3cv.SendRequestAsync().Result;

                                    nodehealth health = new nodehealth();
                                    health.url = rpc.url;
                                    health.rpc = rpc;
                                    health.syncing = syncing;
                                    health.blockheight = latestBlockNumber;
                                    health.nodeVersion = version;
                                    n.healthchecks.Add(health);


                                }
                                catch (Exception ex)
                                {
                                    if (rpc.privateNode)
                                    {
                                        n.errors.Add($"{rpc.name}");
                                    }
                                    else
                                    {
                                        n.errors.Add($"{rpc.url}");
                                    }

                                }
                                finally
                                {
                                    concurrencySemaphore.Release();
                                }



                            });

                            tasks.Add(t);
                        }

                        Task.WaitAll(tasks.ToArray());
                    }




                    foreach(nodehealth nhc in n.healthchecks)
                    {

                        try
                        {
                            blockcheck checkresult = getBlock(nhc.url).Result;

                            nhc.responsetime = checkresult.responsetime;
                        }
                        catch(Exception ex)
                        {

                        }

                    }

                    BigInteger highestblock = n.healthchecks.Max(x => x.blockheight);
                    BigInteger lowestblock = n.healthchecks.Min(x => x.blockheight);
                    BigInteger medianblock = n.healthchecks.OrderByDescending(x => x.blockheight).Skip(Convert.ToInt32(Math.Round(((decimal)n.healthchecks.Count() / (decimal)2), 0))).First().blockheight;

                    foreach (nodehealth healthcheck in n.healthchecks.OrderByDescending(x => x.blockheight).ThenBy(x=>x.responsetime))
                    {


                        BigInteger difference = healthcheck.blockheight - highestblock;

                        if(healthcheck.rpc.privateNode)
                        {
                            Console.WriteLine($"{healthcheck.blockheight} ({difference}) Syncing {healthcheck.syncing} {healthcheck.nodeVersion} {healthcheck.rpc.name} ({healthcheck.responsetime} ms)");
                        }
                        else
                        {
                            Console.WriteLine($"{healthcheck.blockheight} ({difference}) Syncing {healthcheck.syncing} {healthcheck.nodeVersion} {healthcheck.url} ({healthcheck.responsetime} ms)");
                        }

                    }



                    Console.WriteLine($"\nHighest Block Height:{highestblock}");
                    Console.WriteLine($"Median Block Height:{medianblock}");
                    Console.WriteLine($"Lowest Block Height:{lowestblock}\n");

                    Console.WriteLine($"Variance to median: {(highestblock - medianblock) * n.blocktime} seconds");
                    Console.WriteLine($"Variance to slowest: {(highestblock - lowestblock) * n.blocktime} seconds\n");


                    node fastestNode = n.healthchecks.OrderByDescending(x => x.blockheight).Take(1).FirstOrDefault().rpc;
                    node slowestNode = n.healthchecks.OrderBy(x => x.blockheight).Take(1).FirstOrDefault().rpc;

                    node fastestNodeLatency = n.healthchecks.OrderBy(x => x.responsetime).Take(1).FirstOrDefault().rpc;
                    node slowestNodeLatency = n.healthchecks.OrderByDescending(x => x.responsetime).Take(1).FirstOrDefault().rpc;

                    if (fastestNode.privateNode)
                    {
                        Console.WriteLine($"Fastest Node (Sync): {fastestNode.name}");
                    }
                    else
                    {
                        Console.WriteLine($"Fastest Node (Sync): {fastestNode.url}");
                    }

                    if (slowestNode.privateNode)
                    {
                        Console.WriteLine($"Slowest Node (Sync): {slowestNode.name}");
                    }
                    else
                    {
                        Console.WriteLine($"Slowest Node (Sync): {slowestNode.url}");
                    }



                    if (fastestNodeLatency.privateNode)
                    {
                        Console.WriteLine($"Fastest Node (Latency): {fastestNodeLatency.name}");
                    }
                    else
                    {
                        Console.WriteLine($"Fastest Node (Latency): {fastestNodeLatency.url}");
                    }

                    if (slowestNodeLatency.privateNode)
                    {
                        Console.WriteLine($"Slowest Node (Latency): {slowestNodeLatency.name}");
                    }
                    else
                    {
                        Console.WriteLine($"Slowest Node (Latency): {slowestNodeLatency.url}");
                    }

                    if (n.errors.Count > 0)
                    {
                        Console.WriteLine($"Errored: {string.Join(",", n.errors)}");
                    }

                    Console.WriteLine("\n");
                }

                System.Threading.Thread.Sleep(60000);
            }

            Console.ReadLine();
            
        }
    }
}
