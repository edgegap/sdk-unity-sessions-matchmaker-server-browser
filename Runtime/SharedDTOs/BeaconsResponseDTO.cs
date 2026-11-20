using Newtonsoft.Json;

namespace Edgegap
{
    public class BeaconsResponseDTO
    {
        [JsonProperty("count")]
        public string Count;

        [JsonProperty("beacons")]
        public BeaconDTO[] Beacons;

        /// <summary>
        /// Count as a number, null if missing or not a number.
        /// </summary>
        [JsonIgnore]
        public int? BeaconCount => PortMappingDTO.ParseInt(Count);

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }

    public class BeaconDTO
    {
        [JsonProperty("fqdn")]
        public string FullyQualifiedDomainName;

        [JsonProperty("public_ip")]
        public string PublicIP;

        [JsonProperty("tcp_port")]
        public int TcpPort;

        [JsonProperty("udp_port")]
        public int UdpPort;

        [JsonProperty("location")]
        public LocationDTO Location;

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this);
        }
    }
}
