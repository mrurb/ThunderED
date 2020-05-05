using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Wrap;

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
                //HttpStatusCode.ServiceUnavailable // 503 Zkill uses this for rate limiting 
            };

            Polly.Retry.AsyncRetryPolicy<HttpResponseMessage> waitAndRetryPolicy = Policy
                .HandleResult<HttpResponseMessage>(e => e.StatusCode == HttpStatusCode.ServiceUnavailable || e.StatusCode == (System.Net.HttpStatusCode)429)
                .WaitAndRetryAsync(10, attempt => TimeSpan.FromSeconds(0.25 * Math.Pow(2, attempt)), (exception, calculatedWaitDuration) =>
                    {
                        //logger.LogInformation("Zkill is throttling the requests, Delaying for {calculatedWaitDuration.TotalMilliseconds}ms");
                    });

            Polly.CircuitBreaker.AsyncCircuitBreakerPolicy<HttpResponseMessage> circuitBreakerPolicyForRecoverable = Policy
            .HandleResult<HttpResponseMessage>(r => httpStatusCodesWorthRetrying.Contains(r.StatusCode))
            .CircuitBreakerAsync(
                handledEventsAllowedBeforeBreaking: 3,
                durationOfBreak: TimeSpan.FromSeconds(3),
                onBreak: (outcome, breakDelay) =>
                {
                    //logger.LogInformation($"Polly Circuit Breaker logging: Breaking the circuit for {breakDelay.TotalMilliseconds}ms due to: {outcome.Exception?.Message ?? outcome.Result.StatusCode.ToString()}");
                },
                onReset: () => { }, //logger.LogInformation("Polly Circuit Breaker logging: Call ok... closed the circuit again"),
                onHalfOpen: () => { }//logger.LogInformation("Polly Circuit Breaker logging: Half-open: Next call is a trial")
            );

            return Policy.WrapAsync(waitAndRetryPolicy, circuitBreakerPolicyForRecoverable);
        }

    }
}
