using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
        private OrderResponse _orderResponse;

        private OpenOrderProcessor(BinanceClient client, OrderResponse orderResponse)
        {
            this._client = client;
            this._orderResponse = orderResponse;
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
            if (!Properties.Settings.Default.TestMode)
            {
                var cancelOrderResponse = await this._client.CancelOrder(new CancelOrderRequest
                {
                    Symbol = this._orderResponse.Symbol,
                    OrderId = this._orderResponse.OrderId,
                }, Properties.Settings.Default.ReceiveWindow);
                Console.WriteLine($"{nameof(this.MoveUp)}: {DateTime.Now:O} {data.Symbol} Order '{cancelOrderResponse.OrderId}' canceled");
            }

            await this.RefreshOrderResponse();

            Console.WriteLine($"{nameof(this.MoveUp)}: {DateTime.Now:O} {data.Symbol} Creating new order at {newStopPrice:N8}...");
            if (!Properties.Settings.Default.TestMode)
            {
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
        }

        private bool ShouldReorder(BinanceAggregateTradeData data)
        {
            var orderStopPrice = this._orderResponse.StopPrice;
            var lastTradePrice = data.Price;

            if (lastTradePrice <= orderStopPrice)
            {
                // stop-loss is already over last trade price -> it should execute
                this.CheckOrder().ConfigureAwait(false);
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

        private async Task CheckOrder()
        {
            try
            {
                Console.WriteLine($"{nameof(this.CheckOrder)}: {DateTime.Now:O} {this._orderResponse.Symbol} Checking order '{this._orderResponse.OrderId}'...");
                var orderResponse = await this.RefreshOrderResponse();

                switch (orderResponse.Status)
                {
                    case OrderStatus.New:
                    case OrderStatus.PartiallyFilled:
                        Console.WriteLine($"{nameof(this.CheckOrder)}: {DateTime.Now:O} {this._orderResponse.Symbol} Order '{this._orderResponse.OrderId}' status is '{orderResponse.Status}' -> do nothing");
                        return;

                    case OrderStatus.Filled:
                    case OrderStatus.Cancelled:
                    case OrderStatus.PendingCancel:
                    case OrderStatus.Rejected:
                    case OrderStatus.Expired:
                        Console.WriteLine($"{nameof(this.CheckOrder)}: {DateTime.Now:O} {this._orderResponse.Symbol} Order '{this._orderResponse.OrderId}' status is '{orderResponse.Status}' -> stop monitoring");
                        Manager.Remove(orderResponse.OrderId);
                        return;

                    default:
                        throw new ArgumentOutOfRangeException(nameof(orderResponse.Status), $"Invalid OrderStatus '{orderResponse.Status}'");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        private async Task<OrderResponse> RefreshOrderResponse()
        {
            Console.WriteLine($"{nameof(this.RefreshOrderResponse)}: {DateTime.Now:O} {this._orderResponse.Symbol} Refreshing order '{this._orderResponse.OrderId}'...");
            var orderResponse = await this._client.QueryOrder(new QueryOrderRequest
            {
                Symbol = this._orderResponse.Symbol,
                OrderId = this._orderResponse.OrderId
            }, Properties.Settings.Default.ReceiveWindow);

            this._orderResponse = orderResponse;

            return orderResponse;
        }

        public static class Manager
        {
            private static readonly ConcurrentDictionary<long, OpenOrderProcessor> Processors = new ConcurrentDictionary<long, OpenOrderProcessor>();
            private static readonly ConcurrentDictionary<string, HashSet<OpenOrderProcessor>> MonitoredSymbols = new ConcurrentDictionary<string, HashSet<OpenOrderProcessor>>();

            public static void Monitor(BinanceClient client, InstanceBinanceWebSocketClient ws, OrderResponse orderResponse)
            {
                if (!(orderResponse.Type == OrderType.StopLoss || orderResponse.Type == OrderType.StopLossLimit)) return;

                Processors.GetOrAdd(orderResponse.OrderId, _ =>
                {
                    Console.WriteLine($"{nameof(Monitor)}: {DateTime.Now:O} {orderResponse.Symbol} Monitoring '{orderResponse.OrderId}' for {orderResponse.OriginalQuantity - orderResponse.ExecutedQuantity:N8} @ {orderResponse.StopPrice:N8}");

                    var processor = new OpenOrderProcessor(client, orderResponse);
                    var monitoredSymbolsProcessors = MonitoredSymbols.GetOrAdd(orderResponse.Symbol, symbol =>
                    {
                        Monitor(ws, symbol);
                        return new HashSet<OpenOrderProcessor> { processor };
                    });
                    monitoredSymbolsProcessors.Add(processor);
                    return processor;
                });
            }

            public static void Monitor(BinanceClient client, InstanceBinanceWebSocketClient ws, List<OrderResponse> stopLossOrders)
            {
                foreach (var order in stopLossOrders) Monitor(client, ws, order);
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
                Console.WriteLine($"{nameof(Remove)}: {DateTime.Now:O} {processor._orderResponse.Symbol} Stop monitoring '{processor._orderResponse.OrderId}'");

                if (!MonitoredSymbols.TryGetValue(processor._orderResponse.Symbol, out var monitoredSymbolsProcessors)) return;
                monitoredSymbolsProcessors.Remove(processor);
                if (monitoredSymbolsProcessors.Count > 0) return;

                // ReSharper disable once RedundantJumpStatement
                if (!MonitoredSymbols.TryRemove(processor._orderResponse.Symbol, out monitoredSymbolsProcessors)) return;

                // TODO: how to stop receiving messages for this symbol?
            }
        }
    }
}
