using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using ARK_Server_Manager.Lib.Model;
using NeXt.Vdf;

namespace ARK_Server_Manager.Lib
{
    public static class SteamUtils
    {
        public static SteamUserDetailResponse GetSteamUserDetails(List<string> steamIdList)
        {
            const int MAX_IDS = 100;

            SteamUserDetailResponse response = null;

            try
            {
                if (steamIdList.Count == 0)
                    return new SteamUserDetailResponse();

                steamIdList = steamIdList.Distinct().ToList();

                int remainder;
                var totalRequests = Math.DivRem(steamIdList.Count, MAX_IDS, out remainder);
                if (remainder > 0)
                    totalRequests++;

                var requestIndex = 0;
                while (requestIndex < totalRequests)
                {
                    var count = 0;
                    var postData = "";
                    var delimiter = "";
                    for (var index = requestIndex * MAX_IDS; count < MAX_IDS && index < steamIdList.Count; index++)
                    {
                        postData += $"{delimiter}{steamIdList[index]}";
                        delimiter = ",";
                        count++;
                    }

                    var httpRequest = WebRequest.Create($"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={SteamWebApiKey}&format=json&steamids={postData}");
                    httpRequest.Timeout = 30000;
                    var httpResponse = (HttpWebResponse)httpRequest.GetResponse();
                    var responseString = new StreamReader(httpResponse.GetResponseStream()).ReadToEnd();

                    var result = JsonUtils.Deserialize<SteamUserDetailResult>(responseString);
                    if (result != null && result.response != null)
                    {
                        if (response == null)
                            response = result.response;
                        else
                        {
                            response.players.AddRange(result.response.players);
                        }
                    }

                    requestIndex++;
                }

                return response ?? new SteamUserDetailResponse();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ERROR: {nameof(GetSteamUserDetails)}\r\n{ex.Message}");
                return null;
            }
        }

        public static SteamCmdAppManifest ReadSteamCmdAppManifestFile(string file)
        {
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                return null;

            var vdfSerializer = VdfDeserializer.FromFile(file);
            var vdf = vdfSerializer.Deserialize();

            return SteamCmdManifestDetailsResult.Deserialize(vdf);
        }

        public static string SteamWebApiKey
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Config.Default.SteamAPIKey))
                    return Config.Default.SteamAPIKey;
                return Config.Default.DefaultSteamAPIKey;
            }
        }
    }
}
