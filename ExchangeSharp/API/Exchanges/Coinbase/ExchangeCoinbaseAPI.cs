﻿/*
MIT LICENSE

Copyright 2017 Digital Ruby, LLC - http://www.digitalruby.com

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

namespace ExchangeSharp
{
    using ExchangeSharp.Coinbase;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;

    public sealed partial class ExchangeCoinbaseAPI : ExchangeAPI
    {
        public override string BaseUrl { get; set; } = "https://api.pro.coinbase.com";
        public override string BaseUrlWebSocket { get; set; } = "wss://ws-feed.pro.coinbase.com";

        /// <summary>
        /// The response will also contain a CB-AFTER header which will return the cursor id to use in your next request for the page after this one. The page after is an older page and not one that happened after this one in chronological time.
        /// </summary>
        private string cursorAfter;

        /// <summary>
        /// The response will contain a CB-BEFORE header which will return the cursor id to use in your next request for the page before the current one. The page before is a newer page and not one that happened before in chronological time.
        /// </summary>
        private string cursorBefore;

        private ExchangeOrderResult ParseOrder(JToken result)
        {
            decimal executedValue = result["executed_value"].ConvertInvariant<decimal>();
            decimal amountFilled = result["filled_size"].ConvertInvariant<decimal>();
            decimal amount = result["size"].ConvertInvariant<decimal>(amountFilled);
            decimal price = result["price"].ConvertInvariant<decimal>();
            decimal averagePrice = (amountFilled <= 0m ? 0m : executedValue / amountFilled);
            decimal fees = result["fill_fees"].ConvertInvariant<decimal>();
            string symbol = result["id"].ToStringInvariant();

            ExchangeOrderResult order = new ExchangeOrderResult
            {
                Amount = amount,
                AmountFilled = amountFilled,
                Price = price,
                Fees = fees,
                FeesCurrency = symbol.Substring(0, symbol.IndexOf('-')),
                AveragePrice = averagePrice,
                IsBuy = (result["side"].ToStringInvariant() == "buy"),
                OrderDate = result["created_at"].ToDateTimeInvariant(),
                Symbol = symbol,
                OrderId = result["id"].ToStringInvariant()
            };
            switch (result["status"].ToStringInvariant())
            {
                case "pending":
                    order.Result = ExchangeAPIOrderResult.Pending;
                    break;
                case "active":
                case "open":
                    if (order.Amount == order.AmountFilled)
                    {
                        order.Result = ExchangeAPIOrderResult.Filled;
                    }
                    else if (order.AmountFilled > 0.0m)
                    {
                        order.Result = ExchangeAPIOrderResult.FilledPartially;
                    }
                    else
                    {
                        order.Result = ExchangeAPIOrderResult.Pending;
                    }
                    break;
                case "done":
                case "settled":
                    order.Result = ExchangeAPIOrderResult.Filled;
                    break;
                case "cancelled":
                case "canceled":
                    order.Result = ExchangeAPIOrderResult.Canceled;
                    break;
                default:
                    order.Result = ExchangeAPIOrderResult.Unknown;
                    break;
            }
            return order;
        }

        protected override bool CanMakeAuthenticatedRequest(IReadOnlyDictionary<string, object> payload)
        {
            return base.CanMakeAuthenticatedRequest(payload) && Passphrase != null;
        }

        protected override async Task ProcessRequestAsync(HttpWebRequest request, Dictionary<string, object> payload)
        {
            if (CanMakeAuthenticatedRequest(payload))
            {
                // Coinbase is funny and wants a seconds double for the nonce, weird... we convert it to double and back to string invariantly to ensure decimal dot is used and not comma
                string timestamp = payload["nonce"].ToStringInvariant();
                payload.Remove("nonce");
                string form = CryptoUtility.GetJsonForPayload(payload);
                byte[] secret = CryptoUtility.ToBytesBase64Decode(PrivateApiKey);
                string toHash = timestamp + request.Method.ToUpper() + request.RequestUri.PathAndQuery + form;
                string signatureBase64String = CryptoUtility.SHA256SignBase64(toHash, secret);
                secret = null;
                toHash = null;
                request.Headers["CB-ACCESS-KEY"] = PublicApiKey.ToUnsecureString();
                request.Headers["CB-ACCESS-SIGN"] = signatureBase64String;
                request.Headers["CB-ACCESS-TIMESTAMP"] = timestamp;
                request.Headers["CB-ACCESS-PASSPHRASE"] = CryptoUtility.ToUnsecureString(Passphrase);
                await CryptoUtility.WriteToRequestAsync(request, form);
            }
        }

        protected override void ProcessResponse(HttpWebResponse response)
        {
            base.ProcessResponse(response);
            cursorAfter = response.Headers["CB-AFTER"];
            cursorBefore = response.Headers["CB-BEFORE"];
        }

        public ExchangeCoinbaseAPI()
        {
            RequestContentType = "application/json";
            NonceStyle = NonceStyle.UnixSeconds;
        }

        protected override async Task<IEnumerable<ExchangeMarket>> OnGetSymbolsMetadataAsync()
        {
            var markets = new List<ExchangeMarket>();
            JToken products = await MakeJsonRequestAsync<JToken>("/products");
            foreach (JToken product in products)
            {
                var market = new ExchangeMarket
                {
                    MarketName = product["id"].ToStringUpperInvariant(),
                    BaseCurrency = product["quote_currency"].ToStringUpperInvariant(),
                    MarketCurrency = product["base_currency"].ToStringUpperInvariant(),
                    IsActive = string.Equals(product["status"].ToStringInvariant(), "online", StringComparison.OrdinalIgnoreCase),
                    MinTradeSize = product["base_min_size"].ConvertInvariant<decimal>(),
                    PriceStepSize = product["quote_increment"].ConvertInvariant<decimal>()
                };
                markets.Add(market);
            }

            return markets;
        }

        protected override async Task<IEnumerable<string>> OnGetSymbolsAsync()
        {
            return (await GetSymbolsMetadataAsync()).Select(market => market.MarketName);
        }

        protected override async Task<IReadOnlyDictionary<string, ExchangeCurrency>> OnGetCurrenciesAsync()
        {
            var currencies = new Dictionary<string, ExchangeCurrency>();
            JToken products = await MakeJsonRequestAsync<JToken>("/currencies");
            foreach (JToken product in products)
            {
                var currency = new ExchangeCurrency
                {
                    Name = product["id"].ToStringUpperInvariant(),
                    FullName = product["name"].ToStringInvariant(),
                    DepositEnabled = true,
                    WithdrawalEnabled = true
                };

                currencies[currency.Name] = currency;
            }

            return currencies;
        }

        protected override async Task<ExchangeTicker> OnGetTickerAsync(string symbol)
        {
            Dictionary<string, string> ticker = await MakeJsonRequestAsync<Dictionary<string, string>>("/products/" + symbol + "/ticker");
            decimal volume = Convert.ToDecimal(ticker["volume"], System.Globalization.CultureInfo.InvariantCulture);
            DateTime timestamp = ticker["time"].ToDateTimeInvariant();
            decimal price = Convert.ToDecimal(ticker["price"], System.Globalization.CultureInfo.InvariantCulture);
            return new ExchangeTicker
            {
                Ask = Convert.ToDecimal(ticker["ask"], System.Globalization.CultureInfo.InvariantCulture),
                Bid = Convert.ToDecimal(ticker["bid"], System.Globalization.CultureInfo.InvariantCulture),
                Last = price,
                Volume = new ExchangeVolume { BaseVolume = volume, BaseSymbol = symbol, ConvertedVolume = volume * price, ConvertedSymbol = symbol, Timestamp = timestamp }
            };
        }

        protected override async Task<IEnumerable<KeyValuePair<string, ExchangeTicker>>> OnGetTickersAsync()
        {
            List<KeyValuePair<string, ExchangeTicker>> tickers = new List<KeyValuePair<string, ExchangeTicker>>();
            System.Threading.ManualResetEvent evt = new System.Threading.ManualResetEvent(false);
            List<string> symbols = (await GetSymbolsAsync()).ToList();

            // stupid Coinbase does not have a one shot API call for tickers outside of web sockets
            using (var socket = GetTickersWebSocket((t) =>
            {
                lock (tickers)
                {
                    if (symbols.Count != 0)
                    {
                        foreach (var kv in t)
                        {
                            if (!tickers.Exists(m => m.Key == kv.Key))
                            {
                                tickers.Add(kv);
                                symbols.Remove(kv.Key);
                            }
                        }
                        if (symbols.Count == 0)
                        {
                            evt.Set();
                        }
                    }
                }
            }))
            {
                evt.WaitOne(10000);
                return tickers;
            }
        }

        protected override IWebSocket OnGetOrderBookDeltasWebSocket(Action<ExchangeOrderBook> callback, int maxCount = 20, params string[] symbols)
        {
            if (callback == null || symbols == null || symbols.Length == 0)
            {
                return null;
            }

            return ConnectWebSocket(string.Empty, (_socket, msg) =>
            {
                string message = msg.ToStringFromUTF8();
                var book = new ExchangeOrderBook();

                // string comparison on the json text for faster deserialization
                // More likely to be an l2update so check for that first
                if (message.Contains(@"""l2update"""))
                {
                    // parse delta update
                    var delta = JsonConvert.DeserializeObject<Level2>(message);
                    book.Symbol = delta.ProductId;
                    book.SequenceId = delta.Time.Ticks;
                    foreach (string[] change in delta.Changes)
                    {
                        decimal price = change[1].ConvertInvariant<decimal>();
                        decimal amount = change[2].ConvertInvariant<decimal>();
                        if (change[0] == "buy")
                        {
                            book.Bids[price] = new ExchangeOrderPrice { Amount = amount, Price = price };
                        }
                        else
                        {
                            book.Asks[price] = new ExchangeOrderPrice { Amount = amount, Price = price };
                        }
                    }
                }
                else if (message.Contains(@"""snapshot"""))
                {
                    // parse snapshot
                    var snapshot = JsonConvert.DeserializeObject<Snapshot>(message);
                    book.Symbol = snapshot.ProductId;
                    foreach (decimal[] ask in snapshot.Asks)
                    {
                        decimal price = ask[0];
                        decimal amount = ask[1];
                        book.Asks[price] = new ExchangeOrderPrice { Amount = amount, Price = price };
                    }

                    foreach (decimal[] bid in snapshot.Bids)
                    {
                        decimal price = bid[0];
                        decimal amount = bid[1];
                        book.Bids[price] = new ExchangeOrderPrice { Amount = amount, Price = price };
                    }
                }
                else
                {
                    // no other message type handled
                    return Task.CompletedTask;
                }

                callback(book);
                return Task.CompletedTask;
            }, async (_socket) =>
            {
                // subscribe to order book channel for each symbol
                var chan = new Channel { Name = ChannelType.Level2, ProductIds = symbols.ToList() };
                var channelAction = new ChannelAction { Type = ActionType.Subscribe, Channels = new List<Channel> { chan } };
                await _socket.SendMessageAsync(JsonConvert.SerializeObject(channelAction));
            });
        }

        protected override IWebSocket OnGetTickersWebSocket(Action<IReadOnlyCollection<KeyValuePair<string, ExchangeTicker>>> callback)
        {
            return ConnectWebSocket("/", (_socket, msg) =>
            {
                JToken token = JToken.Parse(msg.ToStringFromUTF8());
                if (token["type"].ToStringInvariant() == "ticker")
                {
                    ExchangeTicker ticker = ParseTickerWebSocket(token);
                    callback(new List<KeyValuePair<string, ExchangeTicker>>() { new KeyValuePair<string, ExchangeTicker>(token["product_id"].ToStringInvariant(), ticker) });
                }
                return Task.CompletedTask;
            }, async (_socket) =>
            {
                var symbols = await GetSymbolsAsync();
                var subscribeRequest = new
                {
                    type = "subscribe",
                    product_ids = symbols,
                    channels = new object[]
                    {
                        new
                        {
                            name = "ticker",
                            product_ids = symbols.ToArray()
                        }
                    }
                };
                await _socket.SendMessageAsync(Newtonsoft.Json.JsonConvert.SerializeObject(subscribeRequest));
            });
        }

        private ExchangeTicker ParseTickerWebSocket(JToken token)
        {
            var price = token["price"].ConvertInvariant<decimal>();
            var volume = token["volume_24h"].ConvertInvariant<decimal>();
            var symbol = token["product_id"].ToStringInvariant();
            var time = token["time"].ConvertInvariant<DateTime>(DateTime.UtcNow);
            return new ExchangeTicker
            {
                Ask = token["best_ask"].ConvertInvariant<decimal>(),
                Bid = token["best_bid"].ConvertInvariant<decimal>(),
                Last = price,
                Volume = new ExchangeVolume
                {
                    BaseVolume = volume * price,
                    BaseSymbol = symbol,
                    ConvertedVolume = volume,
                    ConvertedSymbol = symbol,
                    Timestamp = time
                }
            };
        }

        protected override async Task OnGetHistoricalTradesAsync(Func<IEnumerable<ExchangeTrade>, bool> callback, string symbol, DateTime? startDate = null, DateTime? endDate = null)
        {
            /*
            [{
                "time": "2014-11-07T22:19:28.578544Z",
                "trade_id": 74,
                "price": "10.00000000",
                "size": "0.01000000",
                "side": "buy"
            }, {
                "time": "2014-11-07T01:08:43.642366Z",
                "trade_id": 73,
                "price": "100.00000000",
                "size": "0.01000000",
                "side": "sell"
            }]
            */

            ExchangeHistoricalTradeHelper state = new ExchangeHistoricalTradeHelper(this)
            {
                Callback = callback,
                EndDate = endDate,
                ParseFunction = (JToken token) =>
                {
                    return new ExchangeTrade
                    {
                        Amount = token["size"].ConvertInvariant<decimal>(),
                        Id = token["trade_id"].ConvertInvariant<long>(),
                        IsBuy = token["side"].ToStringInvariant() == "buy",
                        Price = token["price"].ConvertInvariant<decimal>(),
                        Timestamp = token["time"].ToDateTimeInvariant()
                    };
                },
                StartDate = startDate,
                Symbol = symbol,
                Url = "/products/[symbol]/trades",
                UrlFunction = (ExchangeHistoricalTradeHelper _state) =>
                {
                    return _state.Url + (string.IsNullOrWhiteSpace(cursorBefore) ? string.Empty : "?before=" + cursorBefore.ToStringInvariant());
                }
            };
            await state.ProcessHistoricalTrades();
        }

        protected override async Task<IEnumerable<ExchangeTrade>> OnGetRecentTradesAsync(string symbol)
        {
            string baseUrl = "/products/" + symbol.ToUpperInvariant() + "/trades";
            Dictionary<string, object>[] trades = await MakeJsonRequestAsync<Dictionary<string, object>[]>(baseUrl);
            List<ExchangeTrade> tradeList = new List<ExchangeTrade>();
            foreach (Dictionary<string, object> trade in trades)
            {
                tradeList.Add(new ExchangeTrade
                {
                    Amount = trade["size"].ConvertInvariant<decimal>(),
                    IsBuy = trade["side"].ToStringInvariant() == "buy",
                    Price = trade["price"].ConvertInvariant<decimal>(),
                    Timestamp = (DateTime)trade["time"],
                    Id = trade["trade_id"].ConvertInvariant<long>()
                });
            }
            return tradeList;
        }

        protected override async Task<ExchangeOrderBook> OnGetOrderBookAsync(string symbol, int maxCount = 50)
        {
            string url = "/products/" + symbol.ToUpperInvariant() + "/book?level=2";
            JToken token = await MakeJsonRequestAsync<JToken>(url);
            return ExchangeAPIExtensions.ParseOrderBookFromJTokenArrays(token, maxCount: maxCount);
        }

        protected override async Task<IEnumerable<MarketCandle>> OnGetCandlesAsync(string symbol, int periodSeconds, DateTime? startDate = null, DateTime? endDate = null, int? limit = null)
        {
            if (limit != null)
            {
                throw new APIException("Limit parameter not supported");
            }

            // /products/<product-id>/candles
            // https://api.pro.coinbase.com/products/LTC-BTC/candles?granularity=86400&start=2017-12-04T18:15:33&end=2017-12-11T18:15:33
            List<MarketCandle> candles = new List<MarketCandle>();
            symbol = NormalizeSymbol(symbol);
            string url = "/products/" + symbol + "/candles?granularity=" + periodSeconds;
            if (startDate == null)
            {
                startDate = DateTime.UtcNow.Subtract(TimeSpan.FromDays(1.0));
            }
            url += "&start=" + startDate.Value.ToString("s", System.Globalization.CultureInfo.InvariantCulture);
            if (endDate == null)
            {
                endDate = DateTime.UtcNow;
            }
            url += "&end=" + endDate.Value.ToString("s", System.Globalization.CultureInfo.InvariantCulture);

            // time, low, high, open, close, volume
            JToken token = await MakeJsonRequestAsync<JToken>(url);
            foreach (JArray candle in token)
            {
                double volume = candle[5].ConvertInvariant<double>();
                decimal close = candle[4].ConvertInvariant<decimal>();
                candles.Add(new MarketCandle
                {
                    ClosePrice = close,
                    ExchangeName = Name,
                    HighPrice = candle[2].ConvertInvariant<decimal>(),
                    LowPrice = candle[1].ConvertInvariant<decimal>(),
                    Name = symbol,
                    OpenPrice = candle[3].ConvertInvariant<decimal>(),
                    PeriodSeconds = periodSeconds,
                    Timestamp = CryptoUtility.UnixTimeStampToDateTimeSeconds(candle[0].ConvertInvariant<long>()),
                    BaseVolume = volume,
                    ConvertedVolume = (volume * (double)close)
                });
            }
            // re-sort in ascending order
            candles.Sort((c1, c2) => c1.Timestamp.CompareTo(c2.Timestamp));
            return candles;
        }

        protected override async Task<Dictionary<string, decimal>> OnGetAmountsAsync()
        {
            Dictionary<string, decimal> amounts = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            JArray array = await MakeJsonRequestAsync<JArray>("/accounts", null, await GetNoncePayloadAsync());
            foreach (JToken token in array)
            {
                decimal amount = token["balance"].ConvertInvariant<decimal>();
                if (amount > 0m)
                {
                    amounts[token["currency"].ToStringInvariant()] = amount;
                }
            }
            return amounts;
        }

        protected override async Task<Dictionary<string, decimal>> OnGetAmountsAvailableToTradeAsync()
        {
            Dictionary<string, decimal> amounts = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            JArray array = await MakeJsonRequestAsync<JArray>("/accounts", null, await GetNoncePayloadAsync());
            foreach (JToken token in array)
            {
                decimal amount = token["available"].ConvertInvariant<decimal>();
                if (amount > 0m)
                {
                    amounts[token["currency"].ToStringInvariant()] = amount;
                }
            }
            return amounts;
        }

        protected override async Task<ExchangeOrderResult> OnPlaceOrderAsync(ExchangeOrderRequest order)
        {
            string symbol = NormalizeSymbol(order.Symbol);
            object nonce = await GenerateNonceAsync();
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "nonce", nonce },
                { "type", order.OrderType.ToStringLowerInvariant() },
                { "side", (order.IsBuy ? "buy" : "sell") },
                { "product_id", symbol },
                { "size", order.RoundAmount().ToStringInvariant() }
            };

            if (order.OrderType != OrderType.Market)
            {
                payload["time_in_force"] = "GTC"; // good til cancel
                payload["price"] = order.Price.ToStringInvariant();
            }

            order.ExtraParameters.CopyTo(payload);
            JToken result = await MakeJsonRequestAsync<JToken>("/orders", null, payload, "POST");
            return ParseOrder(result);
        }

        protected override async Task<ExchangeOrderResult> OnGetOrderDetailsAsync(string orderId, string symbol = null)
        {
            JToken obj = await MakeJsonRequestAsync<JToken>("/orders/" + orderId, null, await GetNoncePayloadAsync());
            return ParseOrder(obj);
        }

        protected override async Task<IEnumerable<ExchangeOrderResult>> OnGetOpenOrderDetailsAsync(string symbol = null)
        {
            List<ExchangeOrderResult> orders = new List<ExchangeOrderResult>();
            symbol = NormalizeSymbol(symbol);
            JArray array = await MakeJsonRequestAsync<JArray>("orders?status=all" + (string.IsNullOrWhiteSpace(symbol) ? string.Empty : "&product_id=" + symbol), null, await GetNoncePayloadAsync());
            foreach (JToken token in array)
            {
                orders.Add(ParseOrder(token));
            }

            return orders;
        }

        protected override async Task<IEnumerable<ExchangeOrderResult>> OnGetCompletedOrderDetailsAsync(string symbol = null, DateTime? afterDate = null)
        {
            List<ExchangeOrderResult> orders = new List<ExchangeOrderResult>();
            symbol = NormalizeSymbol(symbol);
            JArray array = await MakeJsonRequestAsync<JArray>("orders?status=done" + (string.IsNullOrWhiteSpace(symbol) ? string.Empty : "&product_id=" + symbol), null, await GetNoncePayloadAsync());
            foreach (JToken token in array)
            {
                ExchangeOrderResult result = ParseOrder(token);
                if (afterDate == null || result.OrderDate >= afterDate)
                {
                    orders.Add(result);
                }
            }

            return orders;
        }

        protected override async Task OnCancelOrderAsync(string orderId, string symbol = null)
        {
            await MakeJsonRequestAsync<JArray>("orders/" + orderId, null, await GetNoncePayloadAsync(), "DELETE");
        }
    }

    public partial class ExchangeName { public const string Coinbase = "Coinbase"; }
}
