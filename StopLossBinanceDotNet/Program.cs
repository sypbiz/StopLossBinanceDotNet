using System;
using System.Threading.Tasks;
using BinanceExchange.API.Client;
using BinanceExchange.API.Market;
using BinanceExchange.API.Models.Request;
using BinanceExchange.API.Websockets;

namespace StopLossBinanceDotNet
{
    class Program
    {
        static void Main(string[] args)
        {
            MainAsync(args).Wait();
        }

        static async Task MainAsync(string[] args)
        {
            try
            {
                var client = new BinanceClient(new ClientConfiguration
                {
                    ApiKey = Properties.Settings.Default.ApiKey,
                    SecretKey = Properties.Settings.Default.SecretKey
                });

                var ws = new InstanceBinanceWebSocketClient(client);

                var openOrders = await client.GetCurrentOpenOrders(new CurrentOpenOrdersRequest(), Properties.Settings.Default.ReceiveWindow);
                foreach (var order in openOrders)
                {
                    OpenOrderProcessor.Manager.Monitor(client, ws, order);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
    }
}
