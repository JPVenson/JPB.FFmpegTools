using System.Collections.Concurrent;
using System.IO;
using FFMpegCore;
using FFMpegCore.Arguments;
using FFMpegCore.Enums;
using FFMpegCore.Exceptions;
using Polly;

namespace JPB.AudioSubtitleExpander;

public class CopyWithSubStreams : WorkerBase
{
	public override async Task Run()
	{
		var sourceDir = GetInput("ASE_SourceDirectory");
		if (!Directory.Exists(sourceDir))
		{
			Console.WriteLine("Directory does not exist.");
			return;
		}
		var mediaElements = new BlockingCollection<string>();
		var findFilesAsync = mediaElements.FindFilesAsync(sourceDir);

		Console.Write("Positive Match filter: ");
		var positiveMatches = GetInput("ASE_Copy_PositiveLangMatch").Split(';').Select(e => e.Trim().ToLower()).ToArray();
		Console.Write("Negative Match filter: ");
		var negativeMatches = GetInput("ASE_Copy_NegativeLangMatch").Split(';').Select(e => e.Trim().ToLower()).ToArray();

		IEnumerable<MediaStream> FilterNonDesirableStreams(IEnumerable<MediaStream> stream)
		{
			return stream
				.Where(e => positiveMatches.Contains(e.Language?.ToLower()))
				.Where(e => !negativeMatches.Contains(e.Language?.ToLower()));
		}

		var skipped = 0;
		var failed = 0;

		async ValueTask CopyWithStreams(string item, IProgress<double> progressItem)
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

			var audioStreams = FilterNonDesirableStreams(sourceMediaInfo.AudioStreams).ToArray();
			var subtitleStreams = FilterNonDesirableStreams(sourceMediaInfo.SubtitleStreams).ToArray();

			if ((audioStreams.Length == sourceMediaInfo.AudioStreams.Count || audioStreams.Length == 0) &&
				subtitleStreams.Length == sourceMediaInfo.SubtitleStreams.Count)
			{
				Interlocked.Add(ref skipped, 1);
				return;
			}
			//var arguments = $"-i \"{match.target}\" -i \"{match.source}\" -map 0:v:0 -map 1:a -map 1:s -c copy -max_interleave_delta 0 \"{targetFile}\"";

			var outputFileName = Path.GetDirectoryName(item).TrimEnd('/') + "/" +
			                     Path.GetFileNameWithoutExtension(item) + ".ase_work";

			try
			{
				var ffMpegArgumentProcessor = FFMpegArguments.FromFileInput(item, true)
					.OutputToFile(outputFileName, true, f =>
					{
						f.SelectStream(sourceMediaInfo.PrimaryVideoStream.Index);
						f.WithVideoCodec("copy");
						foreach (var audioStream in audioStreams)
						{
							f.SelectStream(audioStream.Index);
						}
						f.WithAudioCodec("copy");

						foreach (var subtitleStream in subtitleStreams)
						{
							f.SelectStream(subtitleStream.Index);
						}
						f.WithArgument(new CustomArgument("-c:s copy"));
						f.WithArgument(new CustomArgument("-map 0:d?"));
						f.WithArgument(new CustomArgument("-map 0:t?"));

						f.WithArgument(new CustomArgument("-copy_unknown"))
							.WithArgument(new CustomArgument("-map_metadata 0"))
							.WithArgument(new CustomArgument("-max_interleave_delta 0"))
							.WithArgument(new CustomArgument("-metadata ase_processing_type=\"remuxed\""))
							.WithArgument(new CustomArgument($"-metadata ase_processing_date=\"{DateTime.Today.Date.ToShortDateString()}\""))
							.ForceFormat("matroska");
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

		await mediaElements.WorkInParallelInteractive(CopyWithStreams,
			(processed, toBeProcessed, itemsPerSec) =>
				$"{processed} / {(!mediaElements.IsAddingCompleted ? "~" : "")}{toBeProcessed + processed} / {itemsPerSec} per sec / failed: {failed} / skipped {skipped}",
			Path.GetFileName,
			1);

		await findFilesAsync;
	}
}