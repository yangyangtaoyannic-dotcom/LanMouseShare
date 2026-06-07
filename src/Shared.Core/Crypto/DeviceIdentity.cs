using System;
using System.Net;
using System.Runtime.Serialization;

namespace LanMouseShare.Core.Crypto
{
    [DataContract]
    public sealed class DeviceIdentity
    {
        [DataMember(Order = 1)]
        public string DeviceId { get; set; }

        [DataMember(Order = 2)]
        public string DeviceName { get; set; }

        [DataMember(Order = 3)]
        public byte[] SharedSecret { get; set; }

        public static DeviceIdentity CreateLocal()
        {
            return new DeviceIdentity
            {
                DeviceId = Guid.NewGuid().ToString("N"),
                DeviceName = Environment.MachineName,
                SharedSecret = SecureRandom.Bytes(32)
            };
        }

        public static string FriendlyName()
        {
            try
            {
                return Dns.GetHostName();
            }
            catch
            {
                return Environment.MachineName;
            }
        }
    }
}
