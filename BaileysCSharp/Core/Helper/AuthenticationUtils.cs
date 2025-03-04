﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BaileysCSharp.Core.Models;
using BaileysCSharp.Core.Types;
using BaileysCSharp.LibSignal;
using static BaileysCSharp.Core.Helper.CryptoUtils;
using static BaileysCSharp.Core.Utils.GenericUtils;
using static BaileysCSharp.LibSignal.KeyHelper;

namespace BaileysCSharp.Core.Helper
{

    public static class AuthenticationUtils
    {

        public static AuthenticationCreds InitAuthCreds()
        {
            var creds = new AuthenticationCreds();
            creds.NoiseKey = Curve.GenerateKeyPair();
            creds.PairingEphemeralKeyPair = Curve.GenerateKeyPair();
            creds.SignedIdentityKey = Curve.GenerateKeyPair();
            creds.SignedPreKey = SignedKeyPair(creds.SignedIdentityKey, 1);
            creds.RegistrationId = GenerateRegistrationId();
            creds.AdvSecretKey = RandomBytes(32).ToBase64();
            creds.ProcessedHistoryMessages = new List<ProcessedHistoryMessage>();
            //creds.
            creds.NextPreKeyId = 1;
            creds.FirstUnuploadedPreKeyId = 1;
            creds.AccountSyncCounter = 0;
            creds.AccountSettings = new AccountSettings()
            {
                UnarchiveChats = false
            };
            // mobile creds
            creds.DeviceId = ConvertToBase64Url(Guid.NewGuid().ToString("N"));
            creds.PhoneId = $"{Guid.NewGuid()}";
            creds.IdentityId = RandomBytes(20);
            creds.Registered = false;
            creds.BackupToken = RandomBytes(20);
            creds.Registration = new Registration() { };
            creds.LastPropHash = default;

            return creds;
        }


        internal static void Randomize(AuthenticationCreds? creds)
        {
            if (creds != null)
            {
                creds.NoiseKey = Curve.GenerateKeyPair();
                creds.AdvSecretKey = RandomBytes(32).ToBase64();
            }
        }
        static string ConvertToBase64Url(string input)
        {
            // Convert hex string to byte array
            byte[] bytes = Enumerable.Range(0, input.Length)
                                     .Where(x => x % 2 == 0)
                                     .Select(x => Convert.ToByte(input.Substring(x, 2), 16))
                                     .ToArray();

            // Convert to base64url
            string base64Url = Convert.ToBase64String(bytes)
                                      .Replace('+', '-')
                                      .Replace('/', '_')
                                      .TrimEnd('=');

            return base64Url;
        }


        public static SignedPreKey SignedKeyPair(KeyPair identityKeyPair, uint keyId)
        {
            var preKey = Curve.GenerateKeyPair();
            var pubKey = GenerateSignalPubKey(preKey.Public);





            var signature = Curve.Sign(identityKeyPair.Private, pubKey);

            return new SignedPreKey()
            {
                Public = preKey.Public,
                Private = preKey.Private,
                Signature = signature,
                KeyId = keyId
            };
        }

    }
}
