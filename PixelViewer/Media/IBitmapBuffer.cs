﻿using Avalonia;
using Avalonia.Media.Imaging;
using Carina.PixelViewer.Runtime.InteropServices;
using CarinaStudio;
using Microsoft.Extensions.Logging;
using System;
using System.Buffers;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Carina.PixelViewer.Media
{
	/// <summary>
	/// Data buffer of <see cref="IBitmap"/>.
	/// </summary>
	unsafe interface IBitmapBuffer : IShareableDisposable<IBitmapBuffer>, IMemoryOwner<byte>
	{
		/// <summary>
		/// Color space of bitmap.
		/// </summary>
		BitmapColorSpace ColorSpace { get; }


		/// <summary>
		/// Format of bitmap.
		/// </summary>
		BitmapFormat Format { get; }


		/// <summary>
		/// Height of bitmap in pixels.
		/// </summary>
		int Height { get; }


		/// <summary>
		/// Bytes per row.
		/// </summary>
		int RowBytes { get; }


		/// <summary>
		/// Width of bitmap in pixels.
		/// </summary>
		int Width { get; }
	}


	/// <summary>
	/// Extensions for <see cref="IBitmapBuffer"/>.
	/// </summary>
	static class BitmapBufferExtensions
	{
		// Function to convert color space.
		unsafe delegate void ColorSpaceConversion(double* r, double* g, double* b);


		// Fields.
		static readonly ILogger? Logger = App.CurrentOrNull?.LoggerFactory?.CreateLogger(nameof(BitmapBufferExtensions));


		/// <summary>
		/// Convert color space of <paramref name="bitmapBuffer"/> to the color space of <paramref name="resultBitmapBuffer"/>.
		/// </summary>
		/// <param name="bitmapBuffer">Source <see cref="IBitmapBuffer"/>.</param>
		/// <param name="resultBitmapBuffer"><see cref="IBitmapBuffer"/> to receive converted data.</param>
		/// <param name="cancellationToken">Cancellation token.</param>
		/// <returns>Task of conversion.</returns>
		public static async Task ConvertToColorSpaceAsync(this IBitmapBuffer bitmapBuffer, IBitmapBuffer resultBitmapBuffer, CancellationToken cancellationToken)
		{
			// check parameters
			if (resultBitmapBuffer == bitmapBuffer)
				throw new ArgumentException("Cannot convert color space in same bitmap buffer.");
			if (bitmapBuffer.Format != resultBitmapBuffer.Format)
				throw new ArgumentException("Cannot convert to bitmap with different formats.");
			if (bitmapBuffer.Width != resultBitmapBuffer.Width || bitmapBuffer.Height != resultBitmapBuffer.Height)
				throw new ArgumentException("Cannot convert to bitmap with different dimensions.");

			// convert
			using var sharedBitmapBuffer = bitmapBuffer.Share();
			using var sharedResultBitmapBuffer = resultBitmapBuffer.Share();
			await Task.Run(() =>
			{
				unsafe
				{
					// select color space converter
					var srcColorSpace = sharedBitmapBuffer.ColorSpace;
					var targetColorSpace = resultBitmapBuffer.ColorSpace;
					var convertFunc = Global.Run(() =>
					{
						if (targetColorSpace == BitmapColorSpace.DCI_P3)
							return (ColorSpaceConversion)srcColorSpace.ConvertToDciP3ColorSpace;
						else if (targetColorSpace == BitmapColorSpace.Srgb)
							return srcColorSpace.ConvertToSrgbColorSpace;
						else
							throw new NotSupportedException($"Unsupported target color space: {resultBitmapBuffer.ColorSpace}");
					});

					// copy directly
					if (srcColorSpace == resultBitmapBuffer.ColorSpace)
					{
						sharedBitmapBuffer.CopyTo(sharedResultBitmapBuffer);
						return;
					}

					// convert to target color space
					var width = sharedBitmapBuffer.Width;
					var srcRowStride = sharedBitmapBuffer.RowBytes;
					var destRowStride = sharedResultBitmapBuffer.RowBytes;
					var stopWatch = App.CurrentOrNull?.IsDebugMode == true
						? new Stopwatch().Also(it => it.Start())
						: null;
					sharedBitmapBuffer.Memory.Pin(srcBaseAddr =>
					{
						sharedResultBitmapBuffer.Memory.Pin(destBaseAddr =>
						{
							switch (sharedBitmapBuffer.Format)
							{
								case BitmapFormat.Bgra32:
									{
										var unpackFunc = ImageProcessing.SelectBgrx32UnpackingAndNormalizing();
										var packFunc = ImageProcessing.SelectBgrx32DenormalizingAndPacking();
										Parallel.For(0, sharedBitmapBuffer.Height, new ParallelOptions() { MaxDegreeOfParallelism = ImageProcessing.SelectMaxDegreeOfParallelism() }, (y) =>
										{
											var b = 0.0;
											var g = 0.0;
											var r = 0.0;
											var a = (byte)0;
											var srcPixelPtr = (uint*)((byte*)srcBaseAddr + (y * srcRowStride));
											var destPixelPtr = (uint*)((byte*)destBaseAddr + (y * destRowStride));
											for (var x = width; x > 0; --x, ++srcPixelPtr, ++destPixelPtr)
											{
												unpackFunc(*srcPixelPtr, &b, &g, &r, &a);
												convertFunc(&r, &g, &b);
												*destPixelPtr = packFunc(b, g, r, a);
											}
											if (cancellationToken.IsCancellationRequested)
												return;
										});
									}
									break;
								case BitmapFormat.Bgra64:
									{
										var unpackFunc = ImageProcessing.SelectBgrx64UnpackingAndNormalizing();
										var packFunc = ImageProcessing.SelectBgrx64DenormalizingAndPacking();
										Parallel.For(0, sharedBitmapBuffer.Height, new ParallelOptions() { MaxDegreeOfParallelism = ImageProcessing.SelectMaxDegreeOfParallelism() }, (y) =>
										{
											var b = 0.0;
											var g = 0.0;
											var r = 0.0;
											var a = (ushort)0;
											var srcPixelPtr = (ulong*)((byte*)srcBaseAddr + (y * srcRowStride));
											var destPixelPtr = (ulong*)((byte*)destBaseAddr + (y * destRowStride));
											for (var x = width; x > 0; --x, ++srcPixelPtr, ++destPixelPtr)
											{
												unpackFunc(*srcPixelPtr, &b, &g, &r, &a);
												convertFunc(&r, &g, &b);
												*destPixelPtr = packFunc(b, g, r, a);
											}
											if (cancellationToken.IsCancellationRequested)
												return;
										});
									}
									break;
							}
						});
					});
					if (cancellationToken.IsCancellationRequested)
						throw new TaskCanceledException();
					if (stopWatch != null)
					{
						stopWatch.Stop();
						Logger?.LogTrace($"Take {stopWatch.ElapsedMilliseconds} ms to convert color space of {width}x{sharedBitmapBuffer.Height} bitmap buffer from {srcColorSpace} to sRGB");
					}
				}
			});
		}


		/// <summary>
		/// Copy data as new bitmap buffer.
		/// </summary>
		/// <param name="source">Source <see cref="IBitmapBuffer"/>.</param>
		/// <returns><see cref="IBitmapBuffer"/> with copied data.</returns>
		public static IBitmapBuffer Copy(this IBitmapBuffer source) => new BitmapBuffer(source.Format, source.ColorSpace, source.Width, source.Height).Also(it =>
		{
			source.CopyTo(it);
		});


		/// <summary>
		/// Copy data to given bitmap buffer.
		/// </summary>
		/// <param name="source">Source <see cref="IBitmapBuffer"/>.</param>
		/// <param name="dest">Destination <see cref="IBitmapBuffer"/>.</param>
		public static unsafe void CopyTo(this IBitmapBuffer source, IBitmapBuffer dest)
		{
			if (source == dest)
				return;
			if (source.Format != dest.Format)
				throw new ArgumentException("Cannot copy to bitmap with different formats.");
			if (source.ColorSpace != dest.ColorSpace)
				throw new ArgumentException("Cannot copy to bitmap with different color spaces.");
			if (source.Width != dest.Width || source.Height != dest.Height)
				throw new ArgumentException("Cannot copy to bitmap with different dimensions.");
			source.Memory.Pin(sourceBaseAddr =>
			{
				dest.Memory.Pin(destBaseAddr =>
				{
					var sourceRowStride = source.RowBytes;
					var destRowStride = dest.RowBytes;
					if (sourceRowStride == destRowStride)
						Marshal.Copy((void*)sourceBaseAddr, (void*)destBaseAddr, sourceRowStride * source.Height);
					else
					{
						var sourceRowPtr = (byte*)sourceBaseAddr;
						var destRowPtr = (byte*)destBaseAddr;
						var minRowStride = Math.Min(sourceRowStride, destRowStride);
						for (var y = source.Height; y > 0; --y, sourceRowPtr += sourceRowStride, destRowPtr += destRowStride)
							Marshal.Copy(sourceRowPtr, destRowPtr, minRowStride);
					}
				});
			});
		}


		/// <summary>
		/// Create <see cref="IBitmap"/> which copied data from this <see cref="IBitmapBuffer"/> asynchronously.
		/// </summary>
		/// <param name="buffer"><see cref="IBitmapBuffer"/>.</param>
		/// <param name="cancellationToken">Cancellation token.</param>
		/// <returns>Task of creating <see cref="IBitmap"/>.</returns>
		public static async Task<IBitmap> CreateAvaloniaBitmapAsync(this IBitmapBuffer buffer, CancellationToken cancellationToken)
        {
			using var sharedBuffer = buffer.Share();
			return await Task.Run(() =>
			{
				return sharedBuffer.Memory.Pin((address) =>
				{
					switch (buffer.Format)
					{
						case BitmapFormat.Bgra32:
							return new Bitmap(Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Unpremul, address, new PixelSize(sharedBuffer.Width, sharedBuffer.Height), new Vector(96, 96), sharedBuffer.RowBytes);

						case BitmapFormat.Bgra64:
                            {
								var avaloniaBitmap = new WriteableBitmap(new PixelSize(sharedBuffer.Width, sharedBuffer.Height), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Unpremul);
								using var avaloniaBitmapBuffer = avaloniaBitmap.Lock();
								var width = buffer.Width;
								var srcRowStride = buffer.RowBytes;
								var destRowStride = avaloniaBitmapBuffer.RowBytes;
								var stopWatch = App.CurrentOrNull?.IsDebugMode == true
									? new Stopwatch().Also(it => it.Start())
									: null;
								buffer.Memory.Pin(srcBaseAddr =>
								{
									unsafe
									{
										var unpackFunc = ImageProcessing.SelectBgra64Unpacking();
										var packFunc = ImageProcessing.SelectBgra32Packing();
										Parallel.For(0, buffer.Height, new ParallelOptions() { MaxDegreeOfParallelism = ImageProcessing.SelectMaxDegreeOfParallelism() }, (y) =>
										{
											var b = (ushort)0;
											var g = (ushort)0;
											var r = (ushort)0;
											var a = (ushort)0;
											var srcPixelPtr = (ulong*)((byte*)srcBaseAddr + (y * srcRowStride));
											var destPixelPtr = (uint*)((byte*)avaloniaBitmapBuffer.Address + (y * destRowStride));
											for (var x = width; x > 0; --x, ++srcPixelPtr, ++destPixelPtr)
											{
												unpackFunc(*srcPixelPtr, &b, &g, &r, &a);
												*destPixelPtr = packFunc((byte)(b >> 8), (byte)(g >> 8), (byte)(r >> 8), (byte)(a >> 8));
											}
											if (cancellationToken.IsCancellationRequested)
												return;
										});
									}
								});
								if (cancellationToken.IsCancellationRequested)
									throw new TaskCanceledException();
								if (stopWatch != null)
								{
									stopWatch.Stop();
									Logger?.LogTrace($"Take {stopWatch.ElapsedMilliseconds} ms to convert from {width}x{buffer.Height} {buffer.Format} bitmap buffer to Avalonia bitmap");
								}
								return avaloniaBitmap;
							}

						default:
							throw new NotSupportedException($"Unsupported bitmap format: {buffer.Format}");
					}
				});
			});
        }


#if WINDOWS10_0_17763_0_OR_GREATER
		/// <summary>
		/// Create <see cref="System.Drawing.Bitmap"/> which copied data from this <see cref="IBitmapBuffer"/>.
		/// </summary>
		/// <param name="buffer"><see cref="IBitmapBuffer"/>.</param>
		/// <returns><see cref="System.Drawing.Bitmap"/>.</returns>
		public static System.Drawing.Bitmap CreateSystemDrawingBitmap(this IBitmapBuffer buffer)
		{
			return buffer.Memory.Pin((address) =>
			{
				return new System.Drawing.Bitmap(buffer.Width, buffer.Height, buffer.RowBytes, buffer.Format.ToSystemDrawingPixelFormat(), address);
			});
		}
#endif


		/// <summary>
		/// Get byte offset to pixel on given position.
		/// </summary>
		/// <param name="buffer"><see cref="IBitmapBuffer"/>.</param>
		/// <param name="x">Horizontal position of pixel.</param>
		/// <param name="y">Vertical position of pixel.</param>
		/// <returns>Byte offset to pixel.</returns>
		public static int GetPixelOffset(this IBitmapBuffer buffer, int x, int y) => (y * buffer.RowBytes) + (x * buffer.Format.GetByteSize());
	}
}
