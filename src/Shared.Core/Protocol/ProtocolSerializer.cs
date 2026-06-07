using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace LanMouseShare.Core.Protocol
{
    public static class ProtocolSerializer
    {
        public static string ToJson<T>(T value)
        {
            if (value == null)
            {
                return "null";
            }

            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public static T FromJson<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return default(T);
            }

            var serializer = new DataContractJsonSerializer(typeof(T));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return (T)serializer.ReadObject(stream);
            }
        }

        public static byte[] ToUtf8JsonBytes<T>(T value)
        {
            return Encoding.UTF8.GetBytes(ToJson(value));
        }

        public static T FromUtf8JsonBytes<T>(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException("bytes");
            }

            return FromJson<T>(Encoding.UTF8.GetString(bytes));
        }
    }
}
