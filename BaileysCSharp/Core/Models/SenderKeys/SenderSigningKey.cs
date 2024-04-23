﻿using Newtonsoft.Json;

namespace BaileysCSharp.Core.Models.SenderKeys
{
    public class SenderSigningKeyStructure
    {
        [JsonProperty("public")]
        public byte[] Public { get; set; }
        [JsonProperty("private")]
        public byte[] Private { get; set; }
    }


}