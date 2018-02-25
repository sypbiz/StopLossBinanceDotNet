using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BinanceExchange.API.Client;
using BinanceExchange.API.Enums;
using BinanceExchange.API.Models.Request;
using BinanceExchange.API.Models.Response;
using BinanceExchange.API.Models.WebSocket;
using BinanceExchange.API.Websockets;

namespace StopLossBinanceDotNet
{
    class OpenOrderProcessor
    {
        private readonly BinanceClient _client;
        private readonly OrderResponse _orderResponse;
        private readonly InstanceBinanceWebSocketClient _ws;

        private OpenOrderProcessor(BinanceClient client, InstanceBinanceWebSocketClient ws, OrderResponse orderResponse)
        {
            this._client = client;
            this._orderResponse = orderResponse;
            this._ws = ws;
        }
        
        private void MessageEventHandler(BinanceAggregateTradeData data)
        {
            try
            {
                Console.WriteLine($"{nameof(this.MessageEventHandler)}: {DateTime.Now:O} {data.Symbol} {data.Price:N8}");
                if (!this.ShouldReorder(data)) return;

                this.MoveUp(data).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        private async Task MoveUp(BinanceAggregateTradeData data)
        {
            var newStopPrice = data.Price * (100 - Properties.Settings.Default.MoveUpPrecentageMarginUnderMarket) / 100;
            Console.WriteLine($"{nameof(this.MoveUp)}: {DateTime.Now:O} {data.Symbol} {data.Price:N8} -> {newStopPrice:N8}");

            Console.WriteLine($"{nameof(this.MoveUp)}: {DateTime.Now:O} {data.Symbol} Canceling order '{this._orderResponse.OrderId}'...");
            var cancelOrderResponse = await this._client.CancelOrder(new CancelOrderRequest
            {
                Symbol = this._orderResponse.Symbol,
                OrderId = this._orderResponse.OrderId,
            }, Properties.Settings.Default.ReceiveWindow);
            Console.WriteLine($"{nameof(this.MoveUp)}: {DateTime.Now:O} {data.Symbol} Order '{cancelOrderResponse.OrderId}' canceled");

            Console.WriteLine($"{nameof(this.MoveUp)}: {DateTime.Now:O} {data.Symbol} Creating new order at {newStopPrice:N8}...");
            var newOrderResponse = await this._client.CreateOrder(new CreateOrderRequest
            {
                Symbol = this._orderResponse.Symbol,
                NewOrderResponseType = NewOrderResponseType.Acknowledge,
                Quantity = this._orderResponse.OriginalQuantity - this._orderResponse.ExecutedQuantity,
                Side = OrderSide.Sell,
                StopPrice = newStopPrice,
                Type = OrderType.StopLoss,
                TimeInForce = TimeInForce.GTC
            });
            Console.WriteLine($"{nameof(this.MoveUp)}: {DateTime.Now:O} {data.Symbol} Order '{newOrderResponse.OrderId}' created");
        }

        private bool ShouldReorder(BinanceAggregateTradeData data)
        {
            var orderStopPrice = this._orderResponse.StopPrice;
            var lastTradePrice = data.Price;

            if (lastTradePrice <= orderStopPrice)
            {
                // stop-loss is already over last trade price -> it should execute
                Task.Factory.StartNew(this.CheckOrder);
                return false;
            }

            // check if last trade price is over thresholds
            if (!this.OverThresholds(data)) return false;

            return true;
        }

        private bool OverThresholds(BinanceAggregateTradeData data)
        {
            var orderStopPrice = this._orderResponse.StopPrice;
            var lastTradePrice = data.Price;

            if (Properties.Settings.Default.MoveUpStaticThreshold > 0 &&
                lastTradePrice > orderStopPrice + Properties.Settings.Default.MoveUpStaticThreshold)
                return true;

            if (Properties.Settings.Default.MoveUpPrcentageThreshold > 0 &&
                lastTradePrice > orderStopPrice * (100 + Properties.Settings.Default.MoveUpPrcentageThreshold) / 100)
                return true;

            // TODO: implement hysteresis

            return false;
        }

        private void CheckOrder()
        {
            throw new NotImplementedException();
        }

        public static class Manager
        {
            private static readonly ConcurrentDictionary<long, OpenOrderProcessor> Processors = new ConcurrentDictionary<long, OpenOrderProcessor>();
            private static readonly ConcurrentDictionary<string, HashSet<OpenOrderProcessor>> MonitoredSymbols = new ConcurrentDictionary<string, HashSet<OpenOrderProcessor>>();

            public static void Monitor(BinanceClient client, InstanceBinanceWebSocketClient ws, OrderResponse orderResponse)
            {
                Processors.GetOrAdd(orderResponse.OrderId, _ =>
                {
                    var processor = new OpenOrderProcessor(client, ws, orderResponse);
                    var monitoredSymbolsProcessors = MonitoredSymbols.GetOrAdd(orderResponse.Symbol, symbol =>
                    {
                        Monitor(ws, symbol);
                        return new HashSet<OpenOrderProcessor> { processor };
                    });
                    monitoredSymbolsProcessors.Add(processor);
                    return processor;
                });
            }

            private static void Monitor(InstanceBinanceWebSocketClient ws, string symbol) => ws.ConnectToTradesWebSocket(symbol, MessageEventDispatcher);
            
            private static void MessageEventDispatcher(BinanceAggregateTradeData data)
            {
                if (!MonitoredSymbols.TryGetValue(data.Symbol, out var monitoredSymbolsProcessors)) return;

                foreach (var orderProcessor in monitoredSymbolsProcessors)
                {
                    Task.Factory.StartNew(() => orderProcessor.MessageEventHandler(data));
                }
            }

            public static void Remove(long orderId)
            {
                if (!Processors.TryRemove(orderId, out var processor)) return;
                if (!MonitoredSymbols.TryGetValue(processor._orderResponse.Symbol, out var monitoredSymbolsProcessors)) return;
                monitoredSymbolsProcessors.Remove(processor);
                if (monitoredSymbolsProcessors.Count > 0) return;

                if (!MonitoredSymbols.TryRemove(processor._orderResponse.Symbol, out monitoredSymbolsProcessors)) return;

                // TODO: how to stop receiving messages for this symbol?
            }
        }
    }
}
