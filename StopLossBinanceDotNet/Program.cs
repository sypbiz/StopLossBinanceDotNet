using System;
using System.Linq;
using System.Threading.Tasks;
using BinanceExchange.API.Client;
using BinanceExchange.API.Enums;
using BinanceExchange.API.Models.Request;
using BinanceExchange.API.Websockets;

namespace StopLossBinanceDotNet
{
    class Program
    {
        // ReSharper disable once UnusedParameter.Local
        static void Main(string[] args)
        {
            MainAsync().Wait();
        }

        static async Task MainAsync()
        {
            try
            {
                var client = new BinanceClient(new ClientConfiguration
                {
                    ApiKey = Properties.Settings.Default.ApiKey,
                    SecretKey = Properties.Settings.Default.SecretKey
                });

                var ws = new InstanceBinanceWebSocketClient(client);

                Console.WriteLine($"{nameof(MainAsync)}: {DateTime.Now:O} Getting currently open orders...");
                var openOrders = await client.GetCurrentOpenOrders(new CurrentOpenOrdersRequest(), Properties.Settings.Default.ReceiveWindow);
                Console.WriteLine($"{nameof(MainAsync)}: {DateTime.Now:O} Found {openOrders.Count} open orders");

                var stopLossOrders = openOrders.Where(o => o.Type == OrderType.StopLoss || o.Type == OrderType.StopLossLimit).ToList();
                Console.WriteLine($"{nameof(MainAsync)}: {DateTime.Now:O} Found {stopLossOrders.Count} open {OrderType.StopLoss}/{OrderType.StopLossLimit} orders");

                OpenOrderProcessor.Manager.Monitor(client, ws, stopLossOrders);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }
    }
}
