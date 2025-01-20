using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FFMpegCore;
using FFMpegCore.Arguments;
using FFMpegCore.Enums;
using FFMpegCore.Exceptions;
using Polly;

namespace JPB.AudioSubtitleExpander
{
	internal class DeinterlaceStreams : WorkerBase
	{
		public DeinterlaceStreams()
		{
			
		}

		public override async Task Run()
		{
			var sourceDir = GetInput("ASE_SourceDirectory");
			if (!Directory.Exists(sourceDir))
			{
				Console.WriteLine("Directory does not exist.");
				return;
			}

			Console.Write("What codec to reencode to: ");
			var codec = GetInput("ASE_Deinterlace_Codec");
			Console.Write("What filter to use to deinterlace: ");
			var deinterlaceFilter = GetInput("ASE_Deinterlace_Filter");
			Console.Write("Reencode CRF: ");
			var crf = int.Parse(GetInput("ASE_Deinterlace_Crf"));

			var mediaElements = new BlockingCollection<string>();
			var findFilesAsync = mediaElements.FindFilesAsync(sourceDir);

			var skipped = 0;
			var failed = 0;
			async ValueTask Deinterlace(string item, IProgress<double> progressItem)
			{
				var sourceMediaInfoResult = await Policy.Handle<Exception>().Or<FFMpegException>()
					.WaitAndRetryAsync(3, i => TimeSpan.FromSeconds(5 + i))
					.ExecuteAndCaptureAsync(async () => await FFProbe.AnalyseAsync(item).ConfigureAwait(false));

				if (sourceMediaInfoResult.FaultType is not null)
				{
					Interlocked.Add(ref skipped, 1);
					return;
				}

				var sourceMediaInfo = sourceMediaInfoResult.Result;

				var outputFileName = Path.GetDirectoryName(item).TrimEnd('/') + "/" +
							 Path.GetFileNameWithoutExtension(item) + ".ase_work";

				try
				{
					var ffMpegArgumentProcessor = FFMpegArguments.FromFileInput(item, true)
						.OutputToFile(outputFileName, true, f =>
						{
							f.SelectStream(sourceMediaInfo.PrimaryVideoStream.Index);
							f.WithVideoCodec(codec);

							f.WithArgument(new CustomArgument($"-vf \"{deinterlaceFilter}\""));
							f.WithSpeedPreset(Speed.VerySlow);
							f.WithConstantRateFactor(crf);
							
							foreach (var audioStream in sourceMediaInfo.AudioStreams)
							{
								f.SelectStream(audioStream.Index);
							}
							f.WithAudioCodec("copy");

							foreach (var subtitleStream in sourceMediaInfo.SubtitleStreams)
							{
								f.SelectStream(subtitleStream.Index);
							}
							f.WithArgument(new CustomArgument("-c:s copy"));
							f.WithArgument(new CustomArgument("-map 0:d?"));
							f.WithArgument(new CustomArgument("-map 0:t?"));

							f.WithArgument(new CustomArgument("-copy_unknown"))
								.WithArgument(new CustomArgument("-map_metadata 0"))
								.WithArgument(new CustomArgument("-max_interleave_delta 0"))
								.WithArgument(new CustomArgument("-metadata ase_processing_type=\"deinterlaced\""))
								.WithArgument(new CustomArgument($"-metadata ase_processing_date=\"{DateTime.Today.Date.ToShortDateString()}\""))
								.ForceFormat("matroska")
								.WithHardwareAcceleration(HardwareAccelerationDevice.CUDA);
						})
						.NotifyOnProgress(progressItem.Report, sourceMediaInfo.Duration);

					var processAsynchronously = await ffMpegArgumentProcessor
						.ProcessAsynchronously().ConfigureAwait(false);

					if (processAsynchronously)
					{
						var oldMetadata = (
							creationDate: File.GetCreationTimeUtc(item),
							modifiedDate: File.GetLastWriteTimeUtc(item),
							accessDate: File.GetLastAccessTimeUtc(item));

						File.Move(outputFileName, item, true);

						File.SetCreationTimeUtc(item, oldMetadata.creationDate);
						File.SetLastWriteTimeUtc(item, oldMetadata.modifiedDate);
						File.SetLastAccessTimeUtc(item, oldMetadata.accessDate);
					}
				}
				catch (Exception e)
				{
					Interlocked.Add(ref failed, 1);
					if (File.Exists(outputFileName))
					{
						File.Delete(outputFileName);
					}
				}

			}

			await mediaElements.WorkInParallelInteractive(Deinterlace,
				(processed, toBeProcessed, itemsPerSec) =>
					$"{processed} / {(!mediaElements.IsAddingCompleted ? "~" : "")}{toBeProcessed + processed} / {itemsPerSec} per sec / failed: {failed} / skipped {skipped}",
				Path.GetFileName,
				1);

			await findFilesAsync;
		}
	}
}
