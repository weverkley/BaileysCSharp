﻿using Proto;
using SkiaSharp;
using BaileysCSharp.Core.Helper;
using BaileysCSharp.Core.Models.Sending.Interfaces;
using static Proto.Message.Types;

namespace BaileysCSharp.Core.Models.Sending.Media
{
    public class ImageMessageContent : AnyMediaMessageContent, IWithDimentions
    {
        private MemoryStream image;

        public ImageMessageContent()
        {
            Property = "ImageMessage";
        }

        public Stream Image
        {
            get => image;

            set => OnLoadImage(value);
        }

        private void OnLoadImage(Stream value)
        {
            if (value is MemoryStream memoryStream)
            {
                image = memoryStream;
            }
            else
            {
                image = new MemoryStream();
                value.CopyTo(image);

            }
            image.Position = 0;
            FileLength = (ulong)image.Length;
        }
        public override async Task Process()
        {
            using (var instream = new MemoryStream())
            {
                image.Position = 0;
                await image.CopyToAsync(instream);
                instream.Position = 0;
                using (var bitmap = SKBitmap.Decode(instream))
                {
                    Height = (uint)bitmap.Height;
                    Width = (uint)bitmap.Width;
                    using (var resized = bitmap.Resize(new SKSizeI(32, 32), SKFilterQuality.None))
                    {
                        using (var stream = new MemoryStream())
                        {
                            resized.Encode(stream, SKEncodedImageFormat.Jpeg, 50);
                            JpegThumbnail = stream.ToArray();
                        }
                    }
                }
                image.Position = 0;
            }
        }

        public string Caption { get; set; }
        public byte[] JpegThumbnail { get; set; }
        public uint Width { get; set; }
        public uint Height { get; set; }
        public ulong FileLength { get; set; }

        public override IMediaMessage ToMediaMessage()
        {
            var image = new ImageMessage()
            {
                ContextInfo = ContextInfo,
                Width = Width,
                Height = Height,
                Mimetype = "image/jpeg",
                JpegThumbnail = JpegThumbnail.ToByteString(),
                MediaKeyTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            if (!string.IsNullOrWhiteSpace(Caption))
            {
                image.Caption = Caption;
            }
            return image;
        }

    }
}
