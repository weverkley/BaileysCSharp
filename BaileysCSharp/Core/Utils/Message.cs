using BaileysCSharp.Core.Extensions;
using BaileysCSharp.Core.Helper;
using BaileysCSharp.Core.Models.Sending;
using BaileysCSharp.Core.Models.Sending.Interfaces;
using BaileysCSharp.Core.Models.Sending.Media;
using BaileysCSharp.Core.Models.Sending.NonMedia;
using BaileysCSharp.Exceptions;
using Proto;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using static BaileysCSharp.Core.Utils.GenericUtils;
using static BaileysCSharp.Core.Utils.JidUtils;
using static BaileysCSharp.Core.Utils.MediaMessageUtil;
using static Proto.Message.Types;

namespace BaileysCSharp.Core.Utils
{
    public class MessageUtil
    {
        public static Message NormalizeMessageContent(Message? content)
        {
            if (content == null)
                return null;

            // set max iterations to prevent an infinite loop
            for (var i = 0; i < 5; i++)
            {
                var inner = GetFutureProofMessage(content);

                if (inner == null)
                {
                    break;

                }

                content = inner.Message;
            }
            return content;
        }



        public static FutureProofMessage? GetFutureProofMessage(Message content)
        {
            return content.EphemeralMessage ??
              content.ViewOnceMessage ??
              content.DocumentWithCaptionMessage ??
              content.ViewOnceMessageV2 ??
              content.ViewOnceMessageV2Extension ??
              content.EditedMessage;
        }

        public static PropertyInfo? GetContentType(Message content)
        {
            if (content == null)
            {
                return null;
            }
            if (content.SenderKeyDistributionMessage != null)
                return null;

            var type = content.GetType();
            var keys = type.GetProperties().Where(x => (x.Name == "Conversation" || (x.Name.Contains("Message")))).ToArray();
            foreach (var key in keys)
            {
                if (key.PropertyType == typeof(bool))
                {
                    continue;
                }

                var value = key.GetValue(content, null);
                if (value != null && value?.ToString() != "")
                {
                    var propertyName = key.Name.Replace("Has", "");
                    var property = type.GetProperty(propertyName);
                    return property;
                }
            }
            return null;
        }


        public static WebMessageInfo GenerateWAMessageFromContent(string jid, Message message, IMessageGenerationOptionsFromContent? options = null)
        {
            var webmessage = new WebMessageInfo();
            if (options?.Quoted != null)
            {
                var quoted = options.Quoted;
                var participant = "";
                if (quoted.Key.FromMe)
                {
                    participant = options.UserJid;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(quoted.Participant))
                    {
                        participant = quoted.Participant;
                    }
                    else if (!string.IsNullOrWhiteSpace(quoted.Key.Participant))
                    {
                        participant = quoted.Key.Participant;
                    }
                    else
                    {
                        participant = quoted.Key.RemoteJid;
                    }
                }

                var quotedMsg = NormalizeMessageContent(quoted.Message);
                var contentType = GetContentType(quotedMsg);
                if (contentType != null)
                {
                    var valueToKeep = contentType.GetValue(quotedMsg, null);
                    quotedMsg = new Message();
                    contentType.SetValue(quotedMsg, valueToKeep);
                }
                var contextInfo = new ContextInfo()
                {
                    Participant = JidNormalizedUser(participant),
                    StanzaId = quoted.Key.Id,
                    QuotedMessage = quotedMsg,
                };
                // if a participant is quoted, then it must be a group
                // hence, remoteJid of group must also be entered
                if (jid != quoted?.Key?.RemoteJid)
                {
                    contextInfo.RemoteJid = quoted.Key.RemoteJid;
                }
                message.SetContextInfo(contextInfo);
            }

            webmessage.Key = new MessageKey()
            {
                FromMe = true,
                Id = options?.MessageID ?? GenerateMessageID(),
                RemoteJid = jid,
            };
            webmessage.Message = message;
            webmessage.MessageTimestamp = (ulong)(DateTimeOffset.Now.ToUnixTimeSeconds());
            webmessage.Status = WebMessageInfo.Types.Status.Pending;

            return webmessage;
        }

        public static async Task<WebMessageInfo> GenerateWAMessage(string jid, IAnyMessageContent content, IMessageGenerationOptions? options = null)
        {
            return GenerateWAMessageFromContent(jid,
                await GenerateWAMessageContent(content, options),
                options);
        }

        public static async Task<Message> GenerateWAMessageContent<T>(T message, IMessageContentGenerationOptions? options = null) where T : IAnyMessageContent
        {
            var m = new Message();

            if (message is TextMessageContent text)
            {
                m.ExtendedTextMessage = new ExtendedTextMessage()
                {
                    Text = text.Text,
                };

                ///TODO generateLinkPreviewIfRequired
            }
            else if (message is ContactMessageContent contact)
            {
                m.ContactMessage = new ContactMessage()
                {
                    DisplayName = contact.Contact.FullName,
                    Vcard = $"BEGIN:VCARD\nVERSION:3.0\nFN:{contact.Contact.FullName}\nORG:;\nTEL;type=CELL;type=VOICE;waid={contact.Contact.ContactNumber}:+{contact.Contact.ContactNumber}\nEND:VCARD"
                };
            }
            else if (message is LocationMessageContent location)
            {
                m.LocationMessage = location.Location;
            }
            else if (message is ReactMessageContent reaction)
            {
                m.ReactionMessage = new ReactionMessage()
                {
                    SenderTimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Key = reaction.Key,
                    Text = reaction.ReactText
                };
            }

            //delete
            //forward
            //disappearingMessagesInChat
            //buttonReply
            //product
            //listReply
            //poll
            //requestPhoneNumber

            else if (message is AnyMediaMessageContent media)
            {
                m = await PrepareWAMessageMedia(media, options);
            }


            //Buttons

            //Sections

            if (message is IViewOnce viewOnce && viewOnce.ViewOnce)
            {
                m = new Message()
                {
                    ViewOnceMessage =
                    {
                        Message = m
                    }
                };
            }

            // Works
            if (message is IMentionable mentionable && mentionable.Mentions?.Length > 0)
            {
                var contentType = GetContentType(m);
                if (contentType != null)
                {
                    var contextInfo = new ContextInfo();
                    contextInfo.MentionedJid.AddRange(mentionable.Mentions);
                    m.SetContextInfo(contextInfo);
                }
            }

            if (message is IButtonable buttonable && buttonable.Buttons?.Length > 0)
            {
                var buttonsMessage = new ButtonsMessage()
                {
                    
                };
                foreach (var item in buttonable.Buttons)
                {
                    buttonsMessage.Buttons.Add(item);
                }

                if (message is TextMessageContent textmessage)
                {
                    buttonsMessage.ContentText = textmessage.Text;
                    buttonsMessage.HeaderType = ButtonsMessage.Types.HeaderType.Empty;
                }
                m = new Message()
                {
                    ButtonsMessage = buttonsMessage
                };
            }

            return m;
        }


        public static Dictionary<string, string> MEDIA_KEYS = new Dictionary<string, string>
        {
            {typeof(ImageMessageContent).Name, "Image" },
            {typeof(AudioMessageContent).Name, "Audio" },
            {typeof(VideoMessageContent).Name, "Video" },
            {typeof(DocumentMessageContent).Name,"Document" }
        };

        private static async Task<Message> PrepareWAMessageMedia<T>(T message, IMediaGenerationOptions? options) where T : AnyMediaMessageContent
        {
            var key = message.GetType().Name;
            if (!MEDIA_KEYS.TryGetValue(key, out string? mediaType))
            {
                mediaType = null;
            }

            // /mms/video
            // /mms/document
            // /mms/audio
            // /mms/image
            // /product/image
            // /mms/md-app-state

            if (string.IsNullOrEmpty(mediaType))
                throw new Boom("Invalid media type", new BoomData(400));

            var uploadData = new MediaUploadData();
            uploadData.CopyMatchingValues(message);


            var mediaData = message.FindMatchingValue<T, Stream>(mediaType);
            if (mediaData == null)
                throw new Boom("Invalid media type", new BoomData(400));

            uploadData.Media = mediaData;


            // check if cacheable + generate cache key
            if (mediaType == "document" && string.IsNullOrEmpty(uploadData.FileName))
            {
                uploadData.FileName = "file";
            }

            // check for cache hit
            ///TODO:
            ///

            var requiresDurationComputation = mediaType == "Audio" && uploadData.Seconds == 0;
            var requiresThumbnailComputation = (mediaType == "Image" || mediaType == "Video") && uploadData.JpegThumbnail == null;

            var requiresWaveformProcessing = mediaType == "Audio" && uploadData.Ptt;
            var requiresAudioBackground = options?.BackgroundColor != null && mediaType == "Audio" && uploadData.Ptt;
            var requiresOriginalForSomeProcessing = requiresDurationComputation || requiresThumbnailComputation;

            var result = EncryptedStream(uploadData.Media, mediaType);

            // url safe Base64 encode the SHA256 hash of the body

            var uploaded = await options.Upload(result.EncWriteStream, new MediaUploadOptions()
            {
                FileEncSha256B64 = result.FileEncSha256.ToBase64(),
                MediaType = mediaType,
                TimeOutMs = options.MediaUploadTimeoutMs
            });

            if (requiresDurationComputation)
            {
                await message.Process();
            }
            if (requiresThumbnailComputation)
            {
                await message.Process();
                if (message is VideoMessageContent video)
                {
                    var thumnailUpload = video.ExtractThumbnail();
                    var thumnailResult = EncryptedStream(thumnailUpload.Media, "thumbnail-link");
                    var thumbnail = await options.Upload(thumnailResult.EncWriteStream, new MediaUploadOptions()
                    {
                        FileEncSha256B64 = thumnailResult.FileEncSha256.ToBase64(),
                        MediaType = "thumbnail-link",
                        TimeOutMs = options.MediaUploadTimeoutMs
                    });
                    video.SetThumbnail(thumbnail, thumnailResult);

                }
            }

            if (requiresWaveformProcessing)
            {

            }

            IMediaMessage? media = message.ToMediaMessage();
            media.CopyMatchingValues(result);
            media.CopyMatchingValues(uploaded);


            //Set Values

            var m = new Message()
            {

            };
            if (media != null)
            {
                m.SetMediaMessage(media, message.Property);
            }
            return m;
        }

        private static MediaEncryptResult EncryptedStream(Stream media, string mediaType)
        {
            var memroyStream = new MemoryStream();
            media.Position = 0;
            media.CopyTo(memroyStream);

            var data = memroyStream.ToArray();
            byte[] encrypted = [];
            //var mediaKey = KeyHelper.RandomBytes(32);
            var mediaKey = Convert.FromBase64String("SrRqGYETTwGcZal0sSwL5NXYc5uGWQK3N+DQ1kqq2aA=");
            var mediaKeys = GetMediaKeys(mediaKey, mediaType);

            var hmac = new HMACSHA256(mediaKeys.MacKey);
            SHA256 sha256Plain = SHA256.Create();
            SHA256 sha256Enc = SHA256.Create();

            Append(hmac, mediaKeys.IV, false);

            using (Aes aesAlg = Aes.Create())
            {
                aesAlg.KeySize = 256;
                aesAlg.Key = mediaKeys.CipherKey;
                aesAlg.IV = mediaKeys.IV;
                aesAlg.Mode = CipherMode.CBC;

                // Create a decryptor to perform the stream transform
                ICryptoTransform encryptor = aesAlg.CreateEncryptor(aesAlg.Key, aesAlg.IV);


                using (MemoryStream msEncrypt = new MemoryStream())
                {
                    using (CryptoStream csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
                    {
                        csEncrypt.Write(data);
                        encrypted = msEncrypt.ToArray();
                        //Plain
                        sha256Plain.TransformFinalBlock(data, 0, data.Length);

                        var size = encrypted.Length;
                        csEncrypt.FlushFinalBlock();
                        encrypted = msEncrypt.ToArray();
                        Append(hmac, encrypted, true);
                        sha256Enc.TransformBlock(encrypted, 0, encrypted.Length, encrypted, 0);
                    }
                }
            }

            var mac = hmac.Hash.Slice(0, 10);
            sha256Enc.TransformFinalBlock(mac, 0, mac.Length);
            var encWriteStream = new MemoryStream();
            encWriteStream.Write(encrypted);
            encWriteStream.Write(mac);
            encWriteStream.Position = 0;


            return new MediaEncryptResult()
            {
                EncWriteStream = encWriteStream,
                FileEncSha256 = sha256Enc.Hash,
                FileLength = (ulong)memroyStream.Length,
                FileSha256 = sha256Plain.Hash,
                Mac = mac,
                MediaKey = mediaKey,
            };
        }

        public static void Append(HMACSHA256 hmac, byte[] buffer, bool isFinal)
        {
            if (!isFinal)
            {
                hmac.TransformBlock(buffer, 0, buffer.Length, buffer, 0);
            }
            else
            {
                hmac.TransformFinalBlock(buffer, 0, buffer.Length);
            }
        }

    }

    public class MediaDownload
    {
        public byte[] Data { get; set; }
        public string MimeType { get; set; }
        public string FileName { get; set; }
        public string Caption { get; set; }
    }

    public class MediaConnInfo
    {
        public string? Auth { get; set; }
        public ulong? Ttl { get; set; }

        public MediaHost[] Hosts { get; set; }
        public DateTime FetchDate { get; set; }
    }

    public class MediaHost
    {
        public string HostName { get; set; }
        public long MaxContentLengthBytes { get; set; }
    }

    public class MediaEncryptResult
    {
        public MemoryStream EncWriteStream { get; set; }
        public byte[] MediaKey { get; set; }
        public byte[] Mac { get; set; }
        public byte[] FileEncSha256 { get; set; }
        public byte[] FileSha256 { get; set; }
        public ulong FileLength { get; set; }
    }

    public class MediaUploadOptions
    {
        public string FileEncSha256B64 { get; set; }
        public string MediaType { get; set; }
        public ulong? TimeOutMs { get; set; }
    }

    public class MediaUploadResult
    {
        [JsonPropertyName("url")]
        public string Url { get; set; }

        [JsonPropertyName("direct_path")]
        public string DirectPath { get; set; }
    }
}
