using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using MySql.Data.MySqlClient.Memcached;
using Newtonsoft.Json;
using Polly.Wrap;
using Polly;
using SQLitePCL;
using System.Linq;
using ThunderED.Helpers;

namespace ThunderED.API
{
    public class ZkillService
    {
        private readonly HttpClient httpClient;
        private readonly string _remoteServiceBaseURL;

        public ZkillService(HttpClient client)
        {
            client.BaseAddress = new Uri("https://zkillboard.com/");
            //client.DefaultRequestHeaders.Add("User-Agent", "");
            httpClient = client;
        }

        public async Task<T> GetWrap<T>(string url) where T : class
        {
            try
            {
                var response = await httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {

                }
                response.EnsureSuccessStatusCode();

                return await WrapAsync<T>(await response.Content.ReadAsStringAsync());
            }
            catch (Exception e)
            {

                throw e;
            }
      
        }

        private async Task<T> WrapAsync<T>(string raw) where T : class
        {
            if (typeof(T) == typeof(string))
                return (T)(object)raw;

            if (!typeof(T).IsClass)
                return null;

            var data = JsonConvert.DeserializeObject<T>(raw);
            if (data == null)
            {
                await LogHelper.LogError($"[try: ][] Deserialized to null!{Environment.NewLine}Request: ", LogCat.ESI, false);
            }
            else return data;
            return null;
        }

        public AsyncPolicyWrap<HttpResponseMessage> DefineAndRetrieveResiliencyStrategy()
        {
            HttpStatusCode[] httpStatusCodesWorthRetrying = {
                HttpStatusCode.InternalServerError, // 500
                HttpStatusCode.BadGateway, // 502
                HttpStatusCode.GatewayTimeout // 504
            };

            var waitAndRetryPolicy = Policy
                .HandleResult<HttpResponseMessage>(e => e.StatusCode == HttpStatusCode.ServiceUnavailable || e.StatusCode == (System.Net.HttpStatusCode)429)
                .WaitAndRetryAsync(10, attempt => TimeSpan.FromSeconds(0.25 * Math.Pow(2, attempt)), async (exception, calculatedWaitDuration) =>
                {
                    await LogHelper.LogInfo("Zkill is throttling the requests, Delaying for {calculatedWaitDuration.TotalMilliseconds}ms");
                });

            var circuitBreakerPolicyForRecoverable = Policy
            .HandleResult<HttpResponseMessage>(r => httpStatusCodesWorthRetrying.Contains(r.StatusCode))
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: 3,
                durationOfBreak: TimeSpan.FromSeconds(3),
                onBreak: async (outcome, breakDelay) =>
                {
                    await LogHelper.LogInfo($"Polly Circuit Breaker logging: Breaking the circuit for {breakDelay.TotalMilliseconds}ms due to: {outcome.Exception?.Message ?? outcome.Result.StatusCode.ToString()}");
                },
                onReset: async () => await LogHelper.LogInfo("Polly Circuit Breaker logging: Call ok... closed the circuit again"),
                onHalfOpen: async () => await LogHelper.LogInfo("Polly Circuit Breaker logging: Half-open: Next call is a trial")
            );

            return Policy.WrapAsync(waitAndRetryPolicy, circuitBreakerPolicyForRecoverable);
        }
    }
}
