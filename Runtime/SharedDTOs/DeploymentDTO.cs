using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;

namespace Edgegap
{
    public class DeploymentDTO
    {
        [JsonProperty("fqdn")]
        public string Fqdn;

        [JsonProperty("public_ip")]
        public string PublicIP;

        [JsonProperty("ports")]
        public Dictionary<string, PortMappingDTO> Ports;

        [JsonProperty("location")]
        public LocationDTO Location;

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }

    public class PortMappingDTO
    {
        [JsonProperty("internal")]
        public string Internal;

        [JsonProperty("external")]
        public string External;

        [JsonProperty("link")]
        public string Link;

        [JsonProperty("protocol")]
        public string Protocol;

        /// <summary>
        /// Internal port as a number, null if missing or not a number.
        /// </summary>
        [JsonIgnore]
        public int? InternalPort => ParseInt(Internal);

        /// <summary>
        /// External port as a number, null if missing or not a number.
        /// </summary>
        [JsonIgnore]
        public int? ExternalPort => ParseInt(External);

        internal static int? ParseInt(string value)
        {
            return int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int number
            )
                ? number
                : null;
        }

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }
}
