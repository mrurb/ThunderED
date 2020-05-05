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
using ThunderED.Classes;

namespace ThunderED.API
{
    public class ZkillService
    {
        private readonly HttpClient httpClient;

        public ZkillService(HttpClient client)
        {
            client.BaseAddress = new Uri("https://zkillboard.com/");
            client.DefaultRequestHeaders.Add("User-Agent", SettingsManager.DefaultUserAgent);
            httpClient = client;
        }

        public async Task<T> GetWrap<T>(string url, string reason) where T : class
        {
            try
            {
                var response = await httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                return await WrapAsync<T>(await response.Content.ReadAsStringAsync(), reason);
            }
            catch (Exception e)
            {

                throw e;
            }
      
        }

        private async Task<T> WrapAsync<T>(string raw, string reason) where T : class
        {
            if (typeof(T) == typeof(string))
                return (T)(object)raw;

            if (!typeof(T).IsClass)
                return null;

            var data = JsonConvert.DeserializeObject<T>(raw);
            if (data == null)
            {
                await LogHelper.LogError($"[try: ][{reason}] Deserialized to null!{Environment.NewLine}Request: ", LogCat.ESI, false);
            }
            else return data;
            return null;
        }
    }
}
