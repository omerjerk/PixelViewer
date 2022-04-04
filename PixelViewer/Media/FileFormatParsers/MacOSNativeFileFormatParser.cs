using Carina.PixelViewer.Media.ImageRenderers;
using Carina.PixelViewer.Media.Profiles;
using Carina.PixelViewer.Native;
using CarinaStudio;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Carina.PixelViewer.Media.FileFormatParsers;

/// <summary>
/// Implementation of <see cref="IFileFormatParser"/> based-on macOS native API.
/// </summary>
abstract class MacOSNativeFileFormatParser : BaseFileFormatParser
{
    // Fields.
    readonly IImageRenderer imageRenderer;


    /// <summary>
    /// Initialize new <see cref="MacOSNativeFileFormatParser"/> instance.
    /// </summary>
    /// <param name="format">File format.</param>
    /// <param name="renderer">Image renderer.</param>
    protected MacOSNativeFileFormatParser(FileFormat format, IImageRenderer renderer) : base(format)
    { 
        this.imageRenderer = renderer;
    }


    /// <summary>
    /// Called to check file header.
    /// </summary>
    /// <param name="stream">Stream to read image data.</param>
    /// <returns>True if header of file is correct.</returns>
    protected abstract bool OnCheckFileHeader(Stream stream);


    /// <summary>
    /// Called to seek stream to position of embedded ICC profile.
    /// </summary>
    /// <param name="stream">Stream to read image data.</param>
    /// <returns>True if seeking successfully.</returns>
    protected virtual bool OnSeekToIccProfile(Stream stream) => false;


    /// <inheritdoc/>
    protected override async Task<ImageRenderingProfile?> ParseImageRenderingProfileAsyncCore(Stream stream, CancellationToken cancellationToken)
    {
        // check file header first to prevent decoding image
        var position = stream.Position;
        var checkingResult = await Task.Run(() => 
        {
            try
            {
                return this.OnCheckFileHeader(stream);
            }
            finally
            {
                stream.Position = position;
            }
        });
        if (!checkingResult)
            return null;

        // check data size
        var dataSize = Math.Max(0, stream.Length - position);
        if (dataSize > 256L << 20) // 256 MB
            return null;
        
        // load ICC profile
        var colorSpaces = await Task.Run(async () =>
        {
            try
            {
                if (!this.OnSeekToIccProfile(stream))
                    return ((Media.ColorSpace, Media.ColorSpace?)?)null;
                return await ColorSpace.LoadFromIccProfileAsync(stream, ColorSpaceSource.Embedded, cancellationToken);
            }
            catch
            {
                return ((Media.ColorSpace, Media.ColorSpace?)?)null;
            }
            finally
            {
                stream.Position = position;
            }
        });
        if (cancellationToken.IsCancellationRequested)
            throw new TaskCanceledException();
        
        // parse image
        var width = 0;
        var height = 0;
        var orientation = 0;
        await Task.Run(() =>
        {
            var imageDataRef = IntPtr.Zero;
            var imageSourceRef = IntPtr.Zero;
            var imagePropertiesRef = IntPtr.Zero;
            try
            {
                // load data into memory
                imageDataRef = MacOS.CFDataCreateMutable(stream, dataSize, cancellationToken);
                
                // create image source
                imageSourceRef = MacOS.CGImageSourceCreateWithData(imageDataRef, IntPtr.Zero);
                if (imageSourceRef == IntPtr.Zero || MacOS.CGImageSourceGetStatus(imageSourceRef) != MacOS.CGImageSourceStatus.Complete)
                    return;
                var primaryImageIndex = MacOS.CGImageSourceGetPrimaryImageIndex(imageSourceRef);
                
                // get dimensions
                imagePropertiesRef = MacOS.CGImageSourceCopyPropertiesAtIndex(imageSourceRef, primaryImageIndex, IntPtr.Zero);
                if (imagePropertiesRef == IntPtr.Zero)
                    return;
                MacOS.CFDictionaryGetValue(imagePropertiesRef, MacOS.kCGImagePropertyPixelWidth, out width);
                MacOS.CFDictionaryGetValue(imagePropertiesRef, MacOS.kCGImagePropertyPixelHeight, out height);
                if (MacOS.CFDictionaryGetValue(imagePropertiesRef, MacOS.kCGImagePropertyOrientation, out int rawOrientation))
                    orientation = Tiff.FromTiffOrientation(rawOrientation);
            }
            finally
            {
                if (imagePropertiesRef != IntPtr.Zero)
                    MacOS.CFRelease(imagePropertiesRef);
                if (imageSourceRef != IntPtr.Zero)
                    MacOS.CFRelease(imageSourceRef);
                if (imageDataRef != IntPtr.Zero)
                    MacOS.CFRelease(imageDataRef);
            }
        });

        // create profile
        if (width <= 0 || height <= 0)
            return null;
        return new ImageRenderingProfile(this.FileFormat, this.imageRenderer).Also(it =>
        {
            if (colorSpaces.HasValue)
                it.ColorSpace = colorSpaces.Value.Let(it => it.Item2 ?? it.Item1);
            it.Height = height;
            it.Orientation = orientation;
            it.Width = width;
        });
    }
}