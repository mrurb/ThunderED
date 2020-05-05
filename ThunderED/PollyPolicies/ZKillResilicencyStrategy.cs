using System;
using System.Linq;
using System.Net;
using System.Net.Http;

using Discord.Net;

using Polly;
using Polly.Wrap;

using ThunderED.Helpers;

// Based on http://www.thepollyproject.org/2018/03/06/policy-recommendations-for-azure-cognitive-services/

namespace ThunderED.PollyPolicies
{
    public static class ZKillResilicencyStrategy
    {
        public static AsyncPolicyWrap<HttpResponseMessage> DefineAndRetrieveResiliencyStrategy()
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
                    const string Message = "Zkill is throttling the requests, Delaying for {calculatedWaitDuration.TotalMilliseconds}ms";
                    await LogHelper.LogInfo(Message);
                });

            var circuitBreakerPolicyForRecoverable = Policy
                .Handle<HttpException>()
                .OrResult<HttpResponseMessage>(r => httpStatusCodesWorthRetrying.Contains(r.StatusCode))
                .CircuitBreakerAsync(
                    handledEventsAllowedBeforeBreaking: 3,
                    durationOfBreak: TimeSpan.FromSeconds(3),
                    onBreak: async (outcome, breakDelay) =>
                    {
                        var message = $"Polly Circuit Breaker logging: Breaking the circuit for {breakDelay.TotalMilliseconds}ms due to: {outcome.Exception?.Message ?? outcome.Result.StatusCode.ToString()}";
                        await LogHelper.LogInfo(message);
                    },
                    onReset: async () =>
                    {
                        const string Message = "Polly Circuit Breaker logging: Call ok... closed the circuit again";
                        await LogHelper.LogInfo(Message);
                    },
                    onHalfOpen: async () =>
                    {
                        const string Message = "Polly Circuit Breaker logging: Half-open: Next call is a trial";
                        await LogHelper.LogInfo(Message);
                    }
                );

            return Policy.WrapAsync(waitAndRetryPolicy, circuitBreakerPolicyForRecoverable);
        }
    }
}
