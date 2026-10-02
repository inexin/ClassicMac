using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ClassicMac.Core;
using ClassicMac.Graphics;
using ClassicMac.Resources;
using ClassicMac.Resources.Decoders.Images;
using ClassicMac.Resources.Decoders.Sound;
using ClassicMac.Resources.Editing;
using CommunityToolkit.Mvvm.Input;

namespace ClassicMac.App.ViewModels
{
    /// <summary>What an import makes: the resource type (or <see cref="MainViewModel.IconFamily"/>), ID and name.</summary>
    public sealed record ImportChoice(string Type, short Id, string Name);

    public sealed partial class MainViewModel
    {
        /// <summary>The import choice that makes every icon of a Finder icon family.</summary>
        public const string IconFamily = "Icon family (ICN#, icl4, icl8, ics#, ics4, ics8)";

        /// <summary>Reads an image file (PNG, JPEG, BMP, GIF…) as RGBA; tests may replace it.</summary>
        internal Func<string, RgbaBitmap> LoadImage { get; set; } = ReadImage;

        // Resource ▸ Import: a PNG (or other image) or WAV file made into a new resource, or into the data of the one
        // of that type and ID, as one undoable edit.
        [RelayCommand(CanExecute = nameof(CanNewResource))]
        private async Task Import()
        {
            if (!await ResolveDraftAsync())
            {
                return;
            }

            if (FileOwner(Selected) is not { } owner || FilePicker is null || EditDialogs is null)
            {
                return;
            }

            if ((await FilePicker.PickFilesAsync()).FirstOrDefault() is not { } path)
            {
                return;
            }

            var fileName = Path.GetFileName(path);
            bool isSound = Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase);
            RgbaBitmap? image = null;
            byte[]? sound = null;
            try
            {
                if (isSound)
                {
                    sound = SoundImport.FromWav(await File.ReadAllBytesAsync(path));
                }
                else
                {
                    image = LoadImage(path);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
            {
                Status = $"“{fileName}” could not be imported: {e.Message}";
                return;
            }

            var fork = StateFor(owner).Session.Fork;
            IReadOnlyList<string> types = isSound ? ["snd "] : [.. ImageImport.Types, IconFamily];
            var selected = (Selected as ResourceNode)?.Resource;
            var type = selected is not null && types.Contains(selected.Type.ToString()) ? selected.Type.ToString() : types[0];
            short id = selected is not null && selected.Type.ToString() == type ? selected.Id : ResourceEditRules.NextFreeId(fork, FourCC.FromString(type));
            if (await EditDialogs.ImportAsync(fileName, types, new ImportChoice(type, id, "")) is not { } choice)
            {
                return;
            }

            IReadOnlyList<(string Type, byte[] Data)> made;
            try
            {
                made = choice.Type == IconFamily ? ImageImport.WriteIconFamily(image!)
                    : [(choice.Type, isSound ? sound! : ImageImport.Write(choice.Type, image!))];
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException)
            {
                Status = e.Message;
                return;
            }

            var edits = new List<IResourceEdit>();
            var replaced = made.Select(m => fork.Find(FourCC.FromString(m.Type), choice.Id)).OfType<Resource>().ToList();
            if (replaced.Count > 0 && !await EditDialogs.ConfirmAsync("Import",
                    $"Replace the data of {string.Join(", ", replaced)} with “{fileName}”?"))
            {
                return;
            }

            Func<Resource?> select = () => null;
            foreach (var (madeType, data) in made)
            {
                if (fork.Find(FourCC.FromString(madeType), choice.Id) is { } existing)
                {
                    edits.Add(new SetResourceData(existing, data));
                    if (edits.Count == 1)
                    {
                        select = () => existing;
                    }

                    continue;
                }
                if (await Validate(fork, new ResourceInfo(madeType, choice.Id, choice.Name, ResourceAttributes.None), null) is not { } valid)
                {
                    return;
                }

                var add = new AddResource(valid.Type, choice.Id, valid.Name, data);
                edits.Add(add);
                if (edits.Count == 1)
                {
                    select = () => add.Added;
                }
            }
            Execute(owner, edits.Count == 1 ? edits[0] : new CompoundEdit($"Import {fileName}", [.. edits]), select);
        }

        // Any image Avalonia decodes, converted to unpremultiplied RGBA.
        private static RgbaBitmap ReadImage(string path)
        {
            using var source = new Bitmap(path);
            var size = source.PixelSize;
            var format = source.Format ?? PixelFormat.Bgra8888;
            bool bgra = format == PixelFormat.Bgra8888;
            if (!bgra && format != PixelFormat.Rgba8888)
            {
                throw new NotSupportedException($"The image's pixel format {format} is not read.");
            }

            var result = new RgbaBitmap(size.Width, size.Height);
            var pixels = result.Pixels;
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                source.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length, size.Width * 4);
            }
            finally
            {
                handle.Free();
            }
            bool premultiplied = source.AlphaFormat == AlphaFormat.Premul;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                if (bgra)
                {
                    (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
                }

                if (source.AlphaFormat == AlphaFormat.Opaque)
                {
                    pixels[i + 3] = 255;
                }
                else if (premultiplied && pixels[i + 3] is > 0 and < 255 and var a)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        pixels[i + c] = (byte)Math.Min(255, (pixels[i + c] * 255 + a / 2) / a);
                    }
                }
            }
            return result;
        }
    }
}
