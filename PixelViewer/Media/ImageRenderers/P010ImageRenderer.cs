﻿using System;

namespace Carina.PixelViewer.Media.ImageRenderers
{
    /// <summary>
    /// <see cref="IImageRenderer"/> which supports rendering image with 10-bit YUV420p based format.
    /// </summary>
    class P010ImageRenderer : BaseYuv420p16ImageRenderer
    {
        public P010ImageRenderer() : base(new ImageFormat(ImageFormatCategory.YUV, "P010", "P010 (10-bit YUV420p)", true, new ImagePlaneDescriptor[] {
            new ImagePlaneDescriptor(2),
            new ImagePlaneDescriptor(2),
            new ImagePlaneDescriptor(2),
        }), 10)
        { }


        // Select UV component.
        protected override void SelectUV(byte uv1, byte uv2, out byte u, out byte v)
        {
            u = uv1;
            v = uv2;
        }
    }
}