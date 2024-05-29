﻿using Google.Protobuf;
using Proto;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using BaileysCSharp.Core.Extensions;
using BaileysCSharp.Core.Helper;
using BaileysCSharp.Core.Models;
using BaileysCSharp.Core.Utils;
using BaileysCSharp.Exceptions;
using static BaileysCSharp.Core.Utils.ProcessMessageUtil;
using static BaileysCSharp.Core.Utils.GenericUtils;
using static BaileysCSharp.Core.WABinary.Constants;
using BaileysCSharp.Core.Events;
using BaileysCSharp.Core.Models.Sending;
using System.Globalization;
using BaileysCSharp.Core.Types;
using BaileysCSharp.Core.WABinary;
using System.Net.Mail;

namespace BaileysCSharp.Core.Sockets
{

    public abstract class MessagesRecvSocket : MessagesSendSocket
    {
        public bool SendActiveReceipts;

        protected MessagesRecvSocket([NotNull] SocketConfig config) : base(config)
        {
            events["CB:message"] = OnMessage;
            events["CB:call"] = OnCall;
            events["CB:receipt"] = OnReceipt;
            events["CB:notification"] = OnNotification;
            events["CB:ack,class:message"] = OnHandleAck;


            EV.Connection.Update += Connection_Update;

        }

        private void Connection_Update(object? sender, ConnectionState e)
        {
            if (e.IsOnline.HasValue)
            {
                SendActiveReceipts = e.IsOnline.Value;
                Logger.Trace($"sendActiveReceipts set to '{SendActiveReceipts}'");
            }
        }

        #region messages-recv


        private static Mutex mut = new Mutex();
        public async Task<bool> ProcessNodeWithBuffer(BinaryNode node, string identifier, Func<BinaryNode, Task> action)
        {
            EV.Buffer();
            mut.WaitOne();
            try
            {
                await action(node);
            }
            catch (Exception ex)
            {
                OnUnexpectedError(ex, identifier);
            }
            EV.Flush();
            mut.ReleaseMutex();
            return true;
        }

        private async Task<bool> OnHandleAck(BinaryNode node)
        {
            return await ProcessNodeWithBuffer(node, "handling ack", HandleAck);
        }

        private async Task<bool> HandleAck(BinaryNode node)
        {
            var key = new MessageKey()
            {
                RemoteJid = node.attrs["from"],
                FromMe = true,
                Id = node.attrs["id"]
            };
            // current hypothesis is that if pash is sent in the ack
            // it means -- the message hasn't reached all devices yet
            // we'll retry sending the message here
            if (node.getattr("phash") != null)
            {
                Logger.Info(new { node.attrs }, "received phash in ack, resending message...");
                var message = this.Store.GetMessage(key);
                if (message != null)
                {
                    await RelayMessage(key.RemoteJid, message, new MessageRelayOptions()
                    {
                        MessageID = key.Id,
                        UseUserDevicesCache = false,
                    });
                }
                else
                {
                    Logger.Warn(new { node.attrs }, "could not send message again, as it was not found");
                }
            }

            return true;
        }

        private async Task<bool> OnNotification(BinaryNode node)
        {
            return await ProcessNodeWithBuffer(node, "handling notification", HandleNotification);
        }

        private async Task HandleNotification(BinaryNode node)
        {
            var remoteJid = node.attrs["from"];
            if (SocketConfig.ShouldIgnoreJid(remoteJid))
            {
                Logger.Debug(new { remoteJid, id = node.attrs["id"] }, "ignored notification");
                SendMessageAck(node);
                return;
            }

            await processingMutex.Mutex(async () =>
            {
                var msg = await ProcessNotifciation(node);
                if (msg != null)
                {
                    var participant = node.getattr("participant");
                    var fromMe = JidUtils.AreJidsSameUser(!string.IsNullOrWhiteSpace(participant) ? participant : remoteJid, Creds.Me.ID);
                    msg.Key = msg.Key ?? new MessageKey();
                    msg.Key.RemoteJid = remoteJid;
                    msg.Key.FromMe = fromMe;
                    msg.Key.Participant = node.getattr("participant");
                    msg.Key.Id = node.getattr("id");
                    msg.Participant = msg.Key.Participant;
                    msg.MessageTimestamp = node.getattr("t").ToUInt64();
                    await UpsertMessage(msg, MessageEventType.Append);
                }
            });

            SendMessageAck(node);

        }


        public async Task<WebMessageInfo?> ProcessNotifciation(BinaryNode node)
        {
            WebMessageInfo? result = default(WebMessageInfo);
            var child = GetAllBinaryNodeChildren(node).FirstOrDefault();

            var nodeType = node.attrs["type"];
            var from = JidUtils.JidNormalizedUser(node.getattr("from"));

            switch (nodeType)
            {
                case "privacy_token":
                    var tokenList = GetBinaryNodeChildren(child, "token");
                    foreach (var item in tokenList)
                    {
                        var jid = item.getattr("jid");
                        var chat = Store.GetChat(jid);
                        if (chat != null)
                        {
                            chat.TcToken = item.ToByteArray();
                            EV.Emit(EmitType.Update, chat);
                        }
                        Logger.Debug(new { jid }, "got privacy token update");
                    }
                    break;
                case "w:gp2":
                    result = new WebMessageInfo();
                    HandleGroupNotification(node.attrs["participant"], child, result);
                    break;
                case "mediaretry":
                    var @event = DecodeMediaRetryNode(node);
                    ///TODO MEDIA RETRY
                    break;
                case "encrypt":
                    await HandleEncryptNotification(node);
                    break;
                case "devices":
                    var devices = GetBinaryNodeChildren(child, "device");
                    if (JidUtils.AreJidsSameUser(child?.getattr("jid"), Creds?.Me.ID))
                    {
                        var deviceJids = devices.Select(x => x.attrs["jid"]).ToArray();
                        Logger.Info(new { deviceJids }, "got my own devices");
                    }
                    break;
                case "server_sync":
                    var update = GetBinaryNodeChild(node, "collection");
                    if (update != null)
                    {
                        var name = update.attrs["name"];
                        await ResyncAppState([name], false);
                    }
                    break;
                case "picture":
                    var setPicture = GetBinaryNodeChild(node, "set");
                    var delPicture = GetBinaryNodeChild(node, "delete");

                    var contact = Store.GetContact(from);
                    if (contact != null)
                    {
                        contact.ImgUrl = (setPicture != null ? "changed" : null);
                        EV.Emit(EmitType.Update, contact);
                    }

                    if (JidUtils.IsJidGroup(from))
                    {
                        result = new WebMessageInfo();
                        var gnode = setPicture ?? delPicture;
                        result.MessageStubType = WebMessageInfo.Types.StubType.GroupChangeIcon;

                        if (setPicture != null)
                        {
                            result.MessageStubParameters.Add(setPicture.attrs["id"]);
                        }

                        result.Participant = node.getattr("author");
                        result.Key = result.Key ?? new MessageKey();
                        result.Key.Participant = setPicture?.getattr("author");
                    }

                    break;
                case "account_sync":
                    if (child?.tag == "disappearing_mode")
                    {
                        var newDuration = child.attrs["duration"].ToUInt64();
                        var timestamp = child.attrs["t"].ToUInt64();

                        Logger.Info(new { newDuration }, "updated account disappearing mode");
                        if (Creds != null)
                        {
                            Creds.AccountSettings.DefaultDissapearingMode = Creds.AccountSettings.DefaultDissapearingMode ?? new DissapearingMode();
                            Creds.AccountSettings.DefaultDissapearingMode.EphemeralExpiration = newDuration;
                            Creds.AccountSettings.DefaultDissapearingMode.EphemeralSettingTimestamp = timestamp;
                            EV.Emit(EmitType.Update, Creds);
                        }
                    }
                    else if (child?.tag == "blocklist")
                    {
                        var blocklists = GetBinaryNodeChildren(child, "item");
                        foreach (var item in blocklists)
                        {
                            var jid = item.getattr("jid");
                            var type = item.getattr("action") == "block" ? "add" : "remove";

                            ///TODO: BLOCKLIST UPDATE
                            //EV.BlockListUpdate([jid], type);
                        }
                    }
                    break;
                case "link_code_companion_reg":
                    //Not sure if this is needed yet.
                    break;

                case "status":
                    var newStatus = GetBinaryNodeChildString(node, "set");
                    contact = Store.GetContact(from);
                    if (contact != null)
                    {
                        contact.Status = newStatus;
                        EV.Emit(EmitType.Update, contact);
                    }
                    break;

                default:
                    Logger.Warn(new { nodeType }, "Node type in Process Notification has not been implemented");

                    break;
            }

            return result;
        }

        private async Task HandleEncryptNotification(BinaryNode node)
        {
            var from = node.attrs["from"];
            if (from == S_WHATSAPP_NET)
            {
                var countChild = GetBinaryNodeChild(node, "count");
                var count = countChild?.getattr("value").ToUInt32();
                var shouldUploadMorePreKeys = count < MIN_PREKEY_COUNT;
                if (shouldUploadMorePreKeys)
                {
                    await UploadPreKeys();
                }
            }
            else
            {
                var identityNode = GetBinaryNodeChild(node, "identity");
                if (identityNode != null)
                {
                    Logger.Info(new { jid = from }, "identity changed");
                    // not handling right now
                    // signal will override new identity anyway
                }
                else
                {
                    Logger.Info(new { node }, "unknown encrypt notification");
                }
            }
        }

        private RetryNode DecodeMediaRetryNode(BinaryNode node)
        {
            var rmrNode = GetBinaryNodeChild(node, "rmr");

            var @event = new RetryNode();

            var errorNode = GetBinaryNodeChild(node, "error");
            if (errorNode != null)
            {
                var errorCode = errorNode.attrs["code"];
                @event.Error = new Boom($"Failed to re-upload media ({errorCode})", new BoomData(MediaMessageUtil.GetStatusCodeForMediaRetry(errorCode), errorNode.attrs));
            }
            else
            {
                var encryptedInfoNode = GetBinaryNodeChild(node, "encrypt");
                var ciphertext = GetBinaryNodeChildBuffer(encryptedInfoNode, "enc_p");
                var iv = GetBinaryNodeChildBuffer(encryptedInfoNode, "enc_iv");

                if (ciphertext != null && iv != null)
                {
                    @event.Media = new RetryMedia { CipherText = ciphertext, IV = iv };
                }
                else
                {
                    @event.Error = new Boom("Failed to re-upload media (missing ciphertext)", new BoomData(404));
                }
            }

            return @event;
        }
        private void HandleGroupNotification(string participant, BinaryNode child, WebMessageInfo msg)
        {
            switch (child.tag)
            {
                case "create":
                    var metadata = ExtractGroupMetaData(child);
                    msg.MessageStubType = WebMessageInfo.Types.StubType.GroupCreate;
                    msg.MessageStubParameters.Add(metadata.Subject);
                    metadata.Author = participant;
                    msg.Key = new MessageKey()
                    {
                        Participant = metadata.Owner,
                    };

                    var chat = new ChatModel()
                    {
                        ID = metadata.ID,
                        Name = metadata.Subject ?? "",
                        ConversationTimestamp = metadata.Creation,
                    };

                    EV.Emit(EmitType.Upsert, chat);
                    EV.Emit(EmitType.Upsert, metadata);

                    break;
                case "ephemeral":
                case "not_ephemeral":
                    msg.Message = new Message()
                    {
                        ProtocolMessage = new Message.Types.ProtocolMessage()
                        {
                            Type = Message.Types.ProtocolMessage.Types.Type.EphemeralSetting,
                            EphemeralExpiration = child.getattr("expiration").ToUInt32(),
                        }
                    };
                    break;
                case "promote":
                case "demote":
                case "remove":
                case "add":
                case "leave":
                    Dictionary<string, WebMessageInfo.Types.StubType> WAMessageStubType = new Dictionary<string, WebMessageInfo.Types.StubType>();
                    foreach (WebMessageInfo.Types.StubType value in System.Enum.GetValues(typeof(WebMessageInfo.Types.StubType)))
                    {
                        WAMessageStubType.Add(value.ToString(), value);
                    }
                    var stubType = $"GroupParticipant{CultureInfo.CurrentCulture.TextInfo.ToTitleCase(child.tag)}";
                    msg.MessageStubType = WAMessageStubType[stubType];
                    var participants = GetBinaryNodeChildren(child, "participant").Select(p => p.attrs["jid"]).ToArray();
                    if (participants.Length == 1 && JidUtils.AreJidsSameUser(participants[0], participant) && child.tag == "remove")
                    {
                        msg.MessageStubType = WebMessageInfo.Types.StubType.GroupParticipantLeave;
                    }
                    msg.MessageStubParameters.AddRange(participants);
                    break;
                case "subject":
                    msg.MessageStubType = WebMessageInfo.Types.StubType.GroupChangeSubject;
                    msg.MessageStubParameters.Add(child.attrs["subject"]);
                    break;
                case "announcement":
                case "not_announcement":
                    msg.MessageStubType = WebMessageInfo.Types.StubType.GroupChangeAnnounce;
                    msg.MessageStubParameters.Add((child.tag == "announcement") ? "on" : "off");
                    break;
                case "locked":
                case "unlocked":
                    msg.MessageStubType = WebMessageInfo.Types.StubType.GroupChangeRestrict;
                    msg.MessageStubParameters.Add((child.tag == "locked") ? "on" : "off");
                    break;
                case "invite":
                    msg.MessageStubType = WebMessageInfo.Types.StubType.GroupChangeInviteLink;
                    msg.MessageStubParameters.Add(child.attrs["code"]);
                    break;
                case "member_add_mode":
                    var addMode = child.content as byte[];
                    if (addMode != null)
                    {
                        msg.MessageStubType = WebMessageInfo.Types.StubType.GroupMemberAddMode;
                        msg.MessageStubParameters.Add(Encoding.UTF8.GetString(addMode));
                    }
                    break;
                case "membership_approval_mode":
                    var approvalMode = GetBinaryNodeChild(child, "group_join");
                    if (approvalMode != null)
                    {
                        msg.MessageStubType = WebMessageInfo.Types.StubType.GroupMembershipJoinApprovalMode;
                        msg.MessageStubParameters.Add(approvalMode.attrs["state"]);

                    }
                    break;
            }
        }


        private async Task<bool> OnReceipt(BinaryNode node)
        {
            return await ProcessNodeWithBuffer(node, "handling receipt", HandleReceipt);
        }

        private Task HandleReceipt(BinaryNode node)
        {

            var isLid = node.getattr("from")?.Contains("lid") ?? false;
            var participant = node.getattr("participant");
            var from = node.getattr("from");
            var recipient = node.getattr("recipient");
            var isNodeFromMe = JidUtils.AreJidsSameUser(participant ?? from, isLid ? Creds.Me.LID : Creds.Me.ID);
            var remoteJid = (!isNodeFromMe || JidUtils.IsJidGroup(from)) ? from : recipient;

            var key = new MessageKey()
            {
                RemoteJid = remoteJid,
                FromMe = true,
                Id = node.attrs["id"]
            };

            List<string> ids = new List<string>() { node.attrs["id"] };
            if (node.content is BinaryNode[] content)
            {
                var items = GetBinaryNodeChildren(content[0], "item");
                foreach (var item in items)
                {
                    ids.Add(item.attrs["id"]);
                }
            }


            if (node.getattr("type") == "retry")
            {
                key.Participant = participant ?? from;
                var retryNode = GetBinaryNodeChild(node, "retry");
                if (key.FromMe)
                {
                    Logger.Debug(new { node.attrs, key }, "recv retry request");
                    try
                    {
                        SendMessageAgain(key, ids, retryNode);
                    }
                    catch (Exception)
                    {

                    }
                }
            }
            else
            {
                if (JidUtils.IsJidGroup(remoteJid))
                {

                }
                else
                {
                    var statuses = Enum.GetValues(typeof(WebMessageInfo.Types.Status))
                        .Cast<WebMessageInfo.Types.Status>()
                        .ToDictionary(t => t.ToString().ToLower(), t => t);

                    var statusCode = node.getattr("type") ?? "";


                    List<MessageReceipt> messages = new List<MessageReceipt>();
                    var t = Convert.ToInt64(node.attrs["t"]);
                    foreach (var item in ids)
                    {

                        var receipt = new MessageReceipt()
                        {
                            MessageID = item,
                            RemoteJid = remoteJid,
                            Status = WebMessageInfo.Types.Status.Pending,
                            Time = t
                        };

                        if (statuses.ContainsKey(statusCode))
                        {
                            receipt.Status = statuses[statusCode];
                        }
                        else
                        {
                            receipt.Status = WebMessageInfo.Types.Status.DeliveryAck;
                        }
                        messages.Add(receipt);
                    }
                    EV.Emit(EmitType.Upsert, messages.ToArray());
                }
            }

            SendMessageAck(node);
            return Task.CompletedTask;
        }

        private async void SendMessageAgain(MessageKey key, List<string> ids, BinaryNode? retryNode)
        {
            var msg = Store.GetMessage(key);

            if (msg != null)
            {
                var remoteJid = key.RemoteJid;
                var participant = !string.IsNullOrWhiteSpace(key.Participant) ? key.Participant : remoteJid;

                var sendToAll = JidUtils.JidDecode(participant)?.Device > 0;

                await AssertSessions([participant], true);

                if (JidUtils.IsJidGroup(remoteJid))
                {
                    //sender here
                }
                Logger.Debug(new { participant, sendToAll }, "forced new session for retry recp");

                //Is there many ?
                //for(let i = 0; i < msgs.length;i++) {
                var msgRelayOpts = new MessageRelayOptions()
                {
                    MessageID = ids[0]
                };

                if (sendToAll)
                {
                    msgRelayOpts.UseUserDevicesCache = false;
                }
                else
                {
                    msgRelayOpts.Participant = new MessageParticipant()
                    {
                        Jid = participant,
                        Count = Convert.ToUInt64(retryNode.attrs["count"]),
                    };
                }

                await RelayMessage(remoteJid, msg, msgRelayOpts);
            }

        }

        private async Task<bool> OnCall(BinaryNode node)
        {
            return await ProcessNodeWithBuffer(node, "handling call", HandleCall);
        }

        private Task HandleCall(BinaryNode node)
        {
            return Task.CompletedTask;
        }

        private async Task<bool> OnMessage(BinaryNode node)
        {
            return await ProcessNodeWithBuffer(node, "processing message", HandleMessage);
        }


        private async Task HandleMessage(BinaryNode node)
        {
            if (GetBinaryNodeChild(node, "unavailable") != null && GetBinaryNodeChild(node, "enc") == null)
            {
                Logger.Debug(node, "missing body; sending ack then ignoring.");
                SendMessageAck(node);
                return;
            }

            await processingMutex.Mutex(async () =>
            {

                var result = MessageDecoder.DecryptMessageNode(node, Creds.Me.ID, Creds.Me.LID, Repository, Logger);
                result.Decrypt();

                if (result.Msg.MessageStubType == WebMessageInfo.Types.StubType.Ciphertext)
                {
                    var encNode = GetBinaryNodeChild(node, "enc");
                    SendRetryRequest(node, encNode != null);
                }
                else
                {
                    // no type in the receipt => message delivered
                    var type = MessageReceiptType.Undefined;
                    var participant = result.Msg.Key.Participant;
                    if (result.Category == "peer") // special peer message
                    {
                        type = MessageReceiptType.PeerMsg;
                    }
                    else if (result.Msg.Key.FromMe) // message was sent by us from a different device
                    {
                        type = MessageReceiptType.Sender;
                        if (JidUtils.IsJidUser(result.Author))
                        {
                            participant = result.Author;
                        }
                        // need to specially handle this case
                    }
                    else if (!SendActiveReceipts)
                    {
                        type = MessageReceiptType.Inactive;
                    }

                    //When upsert works this is to be implemented
                    SendReceipt(result.Msg.Key.RemoteJid, participant, type, result.Msg.Key.Id);

                    var isAnyHistoryMsg = HistoryUtil.GetHistoryMsg(result.Msg.Message);
                    if (isAnyHistoryMsg != null)
                    {
                        var jid = JidUtils.JidNormalizedUser(result.Msg.Key.RemoteJid);
                        SendReceipt(jid, null, MessageReceiptType.HistSync, result.Msg.Key.Id);
                    }
                }

                CleanMessage(result.Msg, Creds.Me.ID);


                await UpsertMessage(result.Msg, node.getattr("offline") != null ? MessageEventType.Append : MessageEventType.Notify);

                //When upsert works this is to be implemented
            });
            SendMessageAck(node);
        }


        private void SendMessageAck(BinaryNode node)
        {
            var stanza = new BinaryNode("ack")
            {
                attrs = new Dictionary<string, string>()
                    {
                        {"id", node.attrs["id"] },
                        {"to", node.attrs["from"] },
                        {"class", node.tag },
                    },
            };
            if (node.attrs.ContainsKey("participant"))
            {
                stanza.attrs["participant"] = node.attrs["participant"];
            }
            if (node.attrs.ContainsKey("recipient"))
            {
                stanza.attrs["recipient"] = node.attrs["recipient"];
            }

            if (node.getattr("type") != null && (node.tag != "message" || GetBinaryNodeChild(node, "unavailable") != null))
            {
                stanza.attrs["type"] = node.attrs["type"];
            }

            if (node.tag == "message" && GetBinaryNodeChild(node, "unavailable") != null)
            {
                stanza.attrs["from"] = Creds.Me.ID;
            }

            Logger.Debug(new { recv = new { node.tag, node.attrs }, sent = stanza.attrs }, "sent ack");
            SendNode(stanza);
        }

        private void SendReceipt(string jid, string participant, string type, params string[] messageIds)
        {

            var node = new BinaryNode("receipt")
            {
                attrs = new Dictionary<string, string>()
                    {
                        {"id", messageIds[0] },
                    },
            };
            if (type == MessageReceiptType.Read || type == MessageReceiptType.ReadSelf)
            {
                node.attrs["t"] = DateTime.Now.UnixTimestampSeconds().ToString();
            }
            if (type == MessageReceiptType.Sender && JidUtils.IsJidUser(jid))
            {
                node.attrs["recipient"] = jid;
                if (!string.IsNullOrWhiteSpace(participant))
                {
                    node.attrs["to"] = participant;
                }
            }
            else
            {
                node.attrs["to"] = jid;
                if (!string.IsNullOrWhiteSpace(participant))
                {
                    node.attrs["recipient"] = participant;
                }
            }

            if (!string.IsNullOrWhiteSpace(type))
            {
                node.attrs["type"] = type;
            }
            var remaining = messageIds.Skip(1).ToArray();
            if (remaining.Length > 0)
            {
                node.content = new BinaryNode("list")
                {
                    content = remaining.Select(x => new BinaryNode("item")
                    {
                        attrs = new Dictionary<string, string>()
                        {
                            { "id", x }
                        }
                    })
                };
            }
            Logger.Info(new { node.attrs, messageIds }, "sending receipt for messages");
            SendNode(node);
        }

        private void SendRetryRequest(BinaryNode node, bool forceIncludeKeys = false)
        {
            var msgId = node.attrs["id"];

            //Check Retries
            if (!MessageRetries.ContainsKey(msgId))
            {
                MessageRetries.Add(msgId, 0);
            }
            var retryCount = MessageRetries[msgId];
            if (retryCount > 5)
            {
                MessageRetries.Remove(msgId);
                return;
            }
            retryCount++;
            MessageRetries[msgId] = retryCount;

            var deviceIdentity = ValidateConnectionUtil.EncodeSignedDeviceIdentity(Creds.Account, true);

            var receipt = new BinaryNode()
            {
                tag = "receipt",
                attrs =
                {
                    {"id", msgId },
                    {"type","retry" },
                    {"to",node.attrs["from"] }
                },
                content = new BinaryNode[]
                {
                    new BinaryNode()
                    {
                        tag = "retry",
                        attrs =
                        {
                            {"count",retryCount.ToString() },
                            {"id", node.attrs["id"] },
                            { "t", node.attrs["t"] },
                            {"v","1" }
                        }
                    },
                    new BinaryNode()
                    {
                        tag = "registration",
                        attrs = { },
                        content = EncodingHelper.EncodeBigEndian(Creds.RegistrationId)
                    }
                }
            };

            if (node.getattr("recipient") != null)
            {
                receipt.attrs["recipient"] = node.attrs["recipient"];
            }
            if (node.getattr("participant") != null)
            {
                receipt.attrs["participant"] = node.attrs["participant"];
            }
            if (node.getattr("participant") != null)
            {
                receipt.attrs["participant"] = node.attrs["participant"];
            }

            if (retryCount > 1 || forceIncludeKeys)
            {
                var preKeys = ValidateConnectionUtil.GetNextPreKeys(Creds, Keys, 1).FirstOrDefault();

                BinaryNode[] content = receipt.content as BinaryNode[];
                Array.Resize(ref content, content.Length + 1);

                content[content.Length - 1] = new BinaryNode()
                {
                    tag = "keys",
                    attrs = { },
                    content = new BinaryNode[]
                    {
                        new BinaryNode(){ tag = "type", content = KEY_BUNDLE_TYPE},
                        new BinaryNode(){ tag = "identity", content = KEY_BUNDLE_TYPE},
                        ValidateConnectionUtil.XmppPreKey(preKeys.Value,preKeys.Key),
                        ValidateConnectionUtil.XmppSignedPreKey(Creds.SignedPreKey),
                        new BinaryNode(){tag = "device-identity", content = deviceIdentity}
                    }

                };

                EV.Emit(EmitType.Update, Creds);
            }

            SendNode(receipt);
            Logger.Info(new { msgAttrs = node.attrs, retryCount }, "sent retry receipt");
        }


        #endregion



        public async Task<MediaDownload> DownloadMediaMessage(WebMessageInfo message)
        {

            if (message.Message.ImageMessage != null)
            {
                var attachement = message.Message.ImageMessage;
                var result = await MediaMessageUtil.DownloadContentFromMessage(new ExternalBlobReference()
                {
                    DirectPath = attachement.DirectPath,
                    FileEncSha256 = attachement.FileEncSha256,
                    FileSha256 = attachement.FileSha256,
                    FileSizeBytes = attachement.FileLength,
                    MediaKey = attachement.MediaKey
                }, "image", new MediaDownloadOptions());
                return new MediaDownload()
                {
                    Data = result,
                    MimeType = attachement.Mimetype,
                    FileName = attachement.Caption + ".jpg",
                    Caption = attachement.Caption,
                };
            }
            if (message.Message.DocumentMessage != null)
            {
                var attachement = message.Message.DocumentMessage;
                var result = await MediaMessageUtil.DownloadContentFromMessage(new ExternalBlobReference()
                {
                    DirectPath = attachement.DirectPath,
                    FileEncSha256 = attachement.FileEncSha256,
                    FileSha256 = attachement.FileSha256,
                    FileSizeBytes = attachement.FileLength,
                    MediaKey = attachement.MediaKey
                }, "document", new MediaDownloadOptions());
                return new MediaDownload()
                {
                    Data = result,
                    MimeType = attachement.Mimetype,
                    FileName = attachement.FileName,
                    Caption = attachement.Caption,
                };
            }
            if (message.Message.AudioMessage != null)
            {
                var attachement = message.Message.AudioMessage;
                var result = await MediaMessageUtil.DownloadContentFromMessage(new ExternalBlobReference()
                {
                    DirectPath = attachement.DirectPath,
                    FileEncSha256 = attachement.FileEncSha256,
                    FileSha256 = attachement.FileSha256,
                    FileSizeBytes = attachement.FileLength,
                    MediaKey = attachement.MediaKey
                }, "audio", new MediaDownloadOptions());
                return new MediaDownload()
                {
                    Data = result,
                    MimeType = attachement.Mimetype,
                };
            }
            if (message.Message.VideoMessage != null)
            {
                var attachement = message.Message.VideoMessage;
                var result = await MediaMessageUtil.DownloadContentFromMessage(new ExternalBlobReference()
                {
                    DirectPath = attachement.DirectPath,
                    FileEncSha256 = attachement.FileEncSha256,
                    FileSha256 = attachement.FileSha256,
                    FileSizeBytes = attachement.FileLength,
                    MediaKey = attachement.MediaKey
                }, "video", new MediaDownloadOptions());
                return new MediaDownload()
                {
                    Data = result,
                    MimeType = attachement.Mimetype,
                    Caption = attachement.Caption,
                };
            }
            if (message.Message.StickerMessage != null)
            {
                var attachement = message.Message.StickerMessage;
                var result = await MediaMessageUtil.DownloadContentFromMessage(new ExternalBlobReference()
                {
                    DirectPath = attachement.DirectPath,
                    FileEncSha256 = attachement.FileEncSha256,
                    FileSha256 = attachement.FileSha256,
                    FileSizeBytes = attachement.FileLength,
                    MediaKey = attachement.MediaKey
                }, "sticker", new MediaDownloadOptions());
                return new MediaDownload()
                {
                    Data = result,
                    MimeType = attachement.Mimetype,
                };
            }
            if (message.Message.PtvMessage != null)
            {
                var attachement = message.Message.PtvMessage;
                var result = await MediaMessageUtil.DownloadContentFromMessage(new ExternalBlobReference()
                {
                    DirectPath = attachement.DirectPath,
                    FileEncSha256 = attachement.FileEncSha256,
                    FileSha256 = attachement.FileSha256,
                    FileSizeBytes = attachement.FileLength,
                    MediaKey = attachement.MediaKey
                }, "ptv", new MediaDownloadOptions());
                return new MediaDownload()
                {
                    Data = result,
                    MimeType = attachement.Mimetype,
                };
            }
            if (message.Message.PtvMessage != null)
            {
                var attachement = message.Message.PtvMessage;
                var result = await MediaMessageUtil.DownloadContentFromMessage(new ExternalBlobReference()
                {
                    DirectPath = attachement.DirectPath,
                    FileEncSha256 = attachement.FileEncSha256,
                    FileSha256 = attachement.FileSha256,
                    FileSizeBytes = attachement.FileLength,
                    MediaKey = attachement.MediaKey
                }, "ptv", new MediaDownloadOptions());
                return new MediaDownload()
                {
                    Data = result,
                    MimeType = attachement.Mimetype,
                };
            }
            if (message.Message.ProductMessage != null)
            {
                var attachement = message.Message.ProductMessage;
                var result = await MediaMessageUtil.DownloadContentFromMessage(new ExternalBlobReference()
                {
                    DirectPath = attachement.Catalog.CatalogImage.DirectPath,
                    FileEncSha256 = attachement.Catalog.CatalogImage.FileEncSha256,
                    FileSha256 = attachement.Catalog.CatalogImage.FileSha256,
                    FileSizeBytes = attachement.Catalog.CatalogImage.FileLength,
                    MediaKey = attachement.Catalog.CatalogImage.MediaKey
                }, "image", new MediaDownloadOptions());
                return new MediaDownload()
                {
                    Data = result,
                    MimeType = attachement.Catalog.CatalogImage.Mimetype,
                };
            }

            throw new NotSupportedException($"{message.Key.Id} does not have a media attached");
        }
    }
}
