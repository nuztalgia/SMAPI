#nullable disable

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using BmFont;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI.Framework.Exceptions;
using StardewModdingAPI.Framework.Reflection;
using StardewModdingAPI.Toolkit.Serialization;
using StardewModdingAPI.Utilities;
using StardewValley;
using xTile;
using xTile.Format;
using xTile.Tiles;

namespace StardewModdingAPI.Framework.ContentManagers
{
    /// <summary>A content manager which handles reading files from a SMAPI mod folder with support for unpacked files.</summary>
    internal class ModContentManager : BaseContentManager
    {
        /*********
        ** Fields
        *********/
        /// <summary>Encapsulates SMAPI's JSON file parsing.</summary>
        private readonly JsonHelper JsonHelper;

        /// <summary>The mod display name to show in errors.</summary>
        private readonly string ModName;

        /// <summary>The game content manager used for map tilesheets not provided by the mod.</summary>
        private readonly IContentManager GameContentManager;

        /// <summary>A case-insensitive lookup of relative paths within the <see cref="ContentManager.RootDirectory"/>.</summary>
        private readonly CaseInsensitivePathCache RelativePathCache;

        /// <summary>If a map tilesheet's image source has no file extensions, the file extensions to check for in the local mod folder.</summary>
        private readonly string[] LocalTilesheetExtensions = { ".png", ".xnb" };


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="name">A name for the mod manager. Not guaranteed to be unique.</param>
        /// <param name="gameContentManager">The game content manager used for map tilesheets not provided by the mod.</param>
        /// <param name="serviceProvider">The service provider to use to locate services.</param>
        /// <param name="modName">The mod display name to show in errors.</param>
        /// <param name="rootDirectory">The root directory to search for content.</param>
        /// <param name="currentCulture">The current culture for which to localize content.</param>
        /// <param name="coordinator">The central coordinator which manages content managers.</param>
        /// <param name="monitor">Encapsulates monitoring and logging.</param>
        /// <param name="reflection">Simplifies access to private code.</param>
        /// <param name="jsonHelper">Encapsulates SMAPI's JSON file parsing.</param>
        /// <param name="onDisposing">A callback to invoke when the content manager is being disposed.</param>
        /// <param name="aggressiveMemoryOptimizations">Whether to enable more aggressive memory optimizations.</param>
        /// <param name="relativePathCache">A case-insensitive lookup of relative paths within the <paramref name="rootDirectory"/>.</param>
        public ModContentManager(string name, IContentManager gameContentManager, IServiceProvider serviceProvider, string modName, string rootDirectory, CultureInfo currentCulture, ContentCoordinator coordinator, IMonitor monitor, Reflector reflection, JsonHelper jsonHelper, Action<BaseContentManager> onDisposing, bool aggressiveMemoryOptimizations, CaseInsensitivePathCache relativePathCache)
            : base(name, serviceProvider, rootDirectory, currentCulture, coordinator, monitor, reflection, onDisposing, isNamespaced: true, aggressiveMemoryOptimizations: aggressiveMemoryOptimizations)
        {
            this.GameContentManager = gameContentManager;
            this.RelativePathCache = relativePathCache;
            this.JsonHelper = jsonHelper;
            this.ModName = modName;

            this.TryLocalizeKeys = false;
        }

        /// <inheritdoc />
        public override bool DoesAssetExist<T>(IAssetName assetName)
        {
            if (base.DoesAssetExist<T>(assetName))
                return true;

            FileInfo file = this.GetModFile(assetName.Name);
            return file.Exists;
        }

        /// <inheritdoc />
        public override T LoadExact<T>(IAssetName assetName, bool useCache)
        {
            // disable caching
            // This is necessary to avoid assets being shared between content managers, which can
            // cause changes to an asset through one content manager affecting the same asset in
            // others (or even fresh content managers). See https://www.patreon.com/posts/27247161
            // for more background info.
            if (useCache)
                throw new InvalidOperationException("Mod content managers don't support asset caching.");

            // resolve managed asset key
            {
                if (this.Coordinator.TryParseManagedAssetKey(assetName.Name, out string contentManagerID, out IAssetName relativePath))
                {
                    if (contentManagerID != this.Name)
                        throw this.GetLoadError(assetName, "can't load a different mod's managed asset key through this mod content manager.");
                    assetName = relativePath;
                }
            }

            // get local asset
            T asset;
            try
            {
                // get file
                FileInfo file = this.GetModFile(assetName.Name);
                if (!file.Exists)
                    throw this.GetLoadError(assetName, "the specified path doesn't exist.");

                // load content
                asset = file.Extension.ToLower() switch
                {
                    ".fnt" => this.LoadFont<T>(assetName, file),
                    ".json" => this.LoadDataFile<T>(assetName, file),
                    ".png" => this.LoadImageFile<T>(assetName, file),
                    ".tbin" or ".tmx" => this.LoadMapFile<T>(assetName, file),
                    ".xnb" => this.LoadXnbFile<T>(assetName),
                    _ => this.HandleUnknownFileType<T>(assetName, file)
                };
            }
            catch (Exception ex) when (ex is not SContentLoadException)
            {
                throw this.GetLoadError(assetName, "an unexpected occurred.", ex);
            }

            // track & return asset
            this.TrackAsset(assetName, asset, useCache: false);
            return asset;
        }

        /// <inheritdoc />
        public override LocalizedContentManager CreateTemporary()
        {
            throw new NotSupportedException("Can't create a temporary mod content manager.");
        }

        /// <summary>Get the underlying key in the game's content cache for an asset. This does not validate whether the asset exists.</summary>
        /// <param name="key">The local path to a content file relative to the mod folder.</param>
        /// <exception cref="ArgumentException">The <paramref name="key"/> is empty or contains invalid characters.</exception>
        public IAssetName GetInternalAssetKey(string key)
        {
            FileInfo file = this.GetModFile(key);
            string relativePath = Path.GetRelativePath(this.RootDirectory, file.FullName);
            string internalKey = Path.Combine(this.Name, relativePath);

            return this.Coordinator.ParseAssetName(internalKey, allowLocales: false);
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Load an unpacked font file (<c>.fnt</c>).</summary>
        /// <typeparam name="T">The type of asset to load.</typeparam>
        /// <param name="assetName">The asset name relative to the loader root directory.</param>
        /// <param name="file">The file to load.</param>
        private T LoadFont<T>(IAssetName assetName, FileInfo file)
        {
            // validate
            if (!typeof(T).IsAssignableFrom(typeof(XmlSource)))
                throw this.GetLoadError(assetName, $"can't read file with extension '{file.Extension}' as type '{typeof(T)}'; must be type '{typeof(XmlSource)}'.");

            // load
            string source = File.ReadAllText(file.FullName);
            return (T)(object)new XmlSource(source);
        }

        /// <summary>Load an unpacked data file (<c>.json</c>).</summary>
        /// <typeparam name="T">The type of asset to load.</typeparam>
        /// <param name="assetName">The asset name relative to the loader root directory.</param>
        /// <param name="file">The file to load.</param>
        private T LoadDataFile<T>(IAssetName assetName, FileInfo file)
        {
            if (!this.JsonHelper.ReadJsonFileIfExists(file.FullName, out T asset))
                throw this.GetLoadError(assetName, "the JSON file is invalid."); // should never happen since we check for file existence before calling this method

            return asset;
        }

        /// <summary>Load an unpacked image file (<c>.json</c>).</summary>
        /// <typeparam name="T">The type of asset to load.</typeparam>
        /// <param name="assetName">The asset name relative to the loader root directory.</param>
        /// <param name="file">The file to load.</param>
        private T LoadImageFile<T>(IAssetName assetName, FileInfo file)
        {
            // validate
            if (typeof(T) != typeof(Texture2D))
                throw this.GetLoadError(assetName, $"can't read file with extension '{file.Extension}' as type '{typeof(T)}'; must be type '{typeof(Texture2D)}'.");

            // load
            using FileStream stream = File.OpenRead(file.FullName);
            Texture2D texture = Texture2D.FromStream(Game1.graphics.GraphicsDevice, stream);
            texture = this.PremultiplyTransparency(texture);
            return (T)(object)texture;
        }

        /// <summary>Load an unpacked image file (<c>.tbin</c> or <c>.tmx</c>).</summary>
        /// <typeparam name="T">The type of asset to load.</typeparam>
        /// <param name="assetName">The asset name relative to the loader root directory.</param>
        /// <param name="file">The file to load.</param>
        private T LoadMapFile<T>(IAssetName assetName, FileInfo file)
        {
            // validate
            if (typeof(T) != typeof(Map))
                throw this.GetLoadError(assetName, $"can't read file with extension '{file.Extension}' as type '{typeof(T)}'; must be type '{typeof(Map)}'.");

            // load
            FormatManager formatManager = FormatManager.Instance;
            Map map = formatManager.LoadMap(file.FullName);
            map.assetPath = assetName.Name;
            this.FixTilesheetPaths(map, relativeMapPath: assetName.Name, fixEagerPathPrefixes: false);
            return (T)(object)map;
        }

        /// <summary>Load a packed file (<c>.xnb</c>).</summary>
        /// <typeparam name="T">The type of asset to load.</typeparam>
        /// <param name="assetName">The asset name relative to the loader root directory.</param>
        private T LoadXnbFile<T>(IAssetName assetName)
        {
            // the underlying content manager adds a .xnb extension implicitly, so
            // we need to strip it here to avoid trying to load a '.xnb.xnb' file.
            IAssetName loadName = assetName.Name.EndsWith(".xnb", StringComparison.OrdinalIgnoreCase)
                ? this.Coordinator.ParseAssetName(assetName.Name[..^".xnb".Length], allowLocales: false)
                : assetName;

            // load asset
            T asset = this.RawLoad<T>(loadName, useCache: false);
            if (asset is Map map)
            {
                map.assetPath = loadName.Name;
                this.FixTilesheetPaths(map, relativeMapPath: loadName.Name, fixEagerPathPrefixes: true);
            }

            return asset;
        }

        /// <summary>Handle a request to load a file type that isn't supported by SMAPI.</summary>
        /// <typeparam name="T">The expected file type.</typeparam>
        /// <param name="assetName">The asset name relative to the loader root directory.</param>
        /// <param name="file">The file to load.</param>
        private T HandleUnknownFileType<T>(IAssetName assetName, FileInfo file)
        {
            throw this.GetLoadError(assetName, $"unknown file extension '{file.Extension}'; must be one of '.fnt', '.json', '.png', '.tbin', '.tmx', or '.xnb'.");
        }

        /// <summary>Get an error which indicates that an asset couldn't be loaded.</summary>
        /// <param name="assetName">The asset name that failed to load.</param>
        /// <param name="reasonPhrase">The reason the file couldn't be loaded.</param>
        /// <param name="exception">The underlying exception, if applicable.</param>
        private SContentLoadException GetLoadError(IAssetName assetName, string reasonPhrase, Exception exception = null)
        {
            return new($"Failed loading asset '{assetName}' from {this.Name}: {reasonPhrase}", exception);
        }

        /// <summary>Get a file from the mod folder.</summary>
        /// <param name="path">The asset path relative to the content folder.</param>
        private FileInfo GetModFile(string path)
        {
            // map to case-insensitive path if needed
            path = this.RelativePathCache.GetFilePath(path);

            // try exact match
            FileInfo file = new(Path.Combine(this.FullRootDirectory, path));

            // try with default extension
            if (!file.Exists)
            {
                foreach (string extension in this.LocalTilesheetExtensions)
                {
                    FileInfo result = new(file.FullName + extension);
                    if (result.Exists)
                    {
                        file = result;
                        break;
                    }
                }
            }

            return file;
        }

        /// <summary>Premultiply a texture's alpha values to avoid transparency issues in the game.</summary>
        /// <param name="texture">The texture to premultiply.</param>
        /// <returns>Returns a premultiplied texture.</returns>
        /// <remarks>Based on <a href="https://gamedev.stackexchange.com/a/26037">code by David Gouveia</a>.</remarks>
        private Texture2D PremultiplyTransparency(Texture2D texture)
        {
            // premultiply pixels
            Color[] data = new Color[texture.Width * texture.Height];
            texture.GetData(data);
            bool changed = false;
            for (int i = 0; i < data.Length; i++)
            {
                Color pixel = data[i];
                if (pixel.A is (byte.MinValue or byte.MaxValue))
                    continue; // no need to change fully transparent/opaque pixels

                data[i] = new Color(pixel.R * pixel.A / byte.MaxValue, pixel.G * pixel.A / byte.MaxValue, pixel.B * pixel.A / byte.MaxValue, pixel.A); // slower version: Color.FromNonPremultiplied(data[i].ToVector4())
                changed = true;
            }

            if (changed)
                texture.SetData(data);

            return texture;
        }

        /// <summary>Fix custom map tilesheet paths so they can be found by the content manager.</summary>
        /// <param name="map">The map whose tilesheets to fix.</param>
        /// <param name="relativeMapPath">The relative map path within the mod folder.</param>
        /// <param name="fixEagerPathPrefixes">Whether to undo the game's eager tilesheet path prefixing for maps loaded from an <c>.xnb</c> file, which incorrectly prefixes tilesheet paths with the map's local asset key folder.</param>
        /// <exception cref="ContentLoadException">A map tilesheet couldn't be resolved.</exception>
        private void FixTilesheetPaths(Map map, string relativeMapPath, bool fixEagerPathPrefixes)
        {
            // get map info
            relativeMapPath = this.AssertAndNormalizeAssetName(relativeMapPath); // Mono's Path.GetDirectoryName doesn't handle Windows dir separators
            string relativeMapFolder = Path.GetDirectoryName(relativeMapPath) ?? ""; // folder path containing the map, relative to the mod folder

            // fix tilesheets
            this.Monitor.VerboseLog($"Fixing tilesheet paths for map '{relativeMapPath}' from mod '{this.ModName}'...");
            foreach (TileSheet tilesheet in map.TileSheets)
            {
                // get image source
                tilesheet.ImageSource = this.NormalizePathSeparators(tilesheet.ImageSource);
                string imageSource = tilesheet.ImageSource;

                // reverse incorrect eager tilesheet path prefixing
                if (fixEagerPathPrefixes && relativeMapFolder.Length > 0 && imageSource.StartsWith(relativeMapFolder))
                    imageSource = imageSource.Substring(relativeMapFolder.Length + 1);

                // validate tilesheet path
                string errorPrefix = $"{this.ModName} loaded map '{relativeMapPath}' with invalid tilesheet path '{imageSource}'.";
                if (Path.IsPathRooted(imageSource) || PathUtilities.GetSegments(imageSource).Contains(".."))
                    throw new SContentLoadException($"{errorPrefix} Tilesheet paths must be a relative path without directory climbing (../).");

                // load best match
                try
                {
                    if (!this.TryGetTilesheetAssetName(relativeMapFolder, imageSource, out IAssetName assetName, out string error))
                        throw new SContentLoadException($"{errorPrefix} {error}");

                    if (!assetName.IsEquivalentTo(tilesheet.ImageSource))
                        this.Monitor.VerboseLog($"   Mapped tilesheet '{tilesheet.ImageSource}' to '{assetName}'.");

                    tilesheet.ImageSource = assetName.Name;
                }
                catch (Exception ex) when (ex is not SContentLoadException)
                {
                    throw new SContentLoadException($"{errorPrefix} The tilesheet couldn't be loaded.", ex);
                }
            }
        }

        /// <summary>Get the actual asset name for a tilesheet.</summary>
        /// <param name="modRelativeMapFolder">The folder path containing the map, relative to the mod folder.</param>
        /// <param name="relativePath">The tilesheet path to load.</param>
        /// <param name="assetName">The found asset name.</param>
        /// <param name="error">A message indicating why the file couldn't be loaded.</param>
        /// <returns>Returns whether the asset name was found.</returns>
        /// <remarks>See remarks on <see cref="FixTilesheetPaths"/>.</remarks>
        private bool TryGetTilesheetAssetName(string modRelativeMapFolder, string relativePath, out IAssetName assetName, out string error)
        {
            assetName = null;
            error = null;

            // nothing to do
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                assetName = null;
                return true;
            }

            // special case: local filenames starting with a dot should be ignored
            // For example, this lets mod authors have a '.spring_town.png' file in their map folder so it can be
            // opened in Tiled, while still mapping it to the vanilla 'Maps/spring_town' asset at runtime.
            {
                string filename = Path.GetFileName(relativePath);
                if (filename.StartsWith("."))
                    relativePath = Path.Combine(Path.GetDirectoryName(relativePath) ?? "", filename.TrimStart('.'));
            }

            // get relative to map file
            {
                string localKey = Path.Combine(modRelativeMapFolder, relativePath);
                if (this.GetModFile(localKey).Exists)
                {
                    assetName = this.GetInternalAssetKey(localKey);
                    return true;
                }
            }

            // get from game assets
            IAssetName contentKey = this.Coordinator.ParseAssetName(this.GetContentKeyForTilesheetImageSource(relativePath), allowLocales: false);
            try
            {
                this.GameContentManager.LoadLocalized<Texture2D>(contentKey, this.GameContentManager.Language, useCache: true); // no need to bypass cache here, since we're not storing the asset
                assetName = contentKey;
                return true;
            }
            catch
            {
                // ignore file-not-found errors
                // TODO: while it's useful to suppress an asset-not-found error here to avoid
                // confusion, this is a pretty naive approach. Even if the file doesn't exist,
                // the file may have been loaded through an IAssetLoader which failed. So even
                // if the content file doesn't exist, that doesn't mean the error here is a
                // content-not-found error. Unfortunately XNA doesn't provide a good way to
                // detect the error type.
                if (this.GetContentFolderFileExists(contentKey.Name))
                    throw;
            }

            // not found
            error = "The tilesheet couldn't be found relative to either map file or the game's content folder.";
            return false;
        }

        /// <summary>Get whether a file from the game's content folder exists.</summary>
        /// <param name="key">The asset key.</param>
        private bool GetContentFolderFileExists(string key)
        {
            // get file path
            string path = Path.Combine(this.GameContentManager.FullRootDirectory, key);
            if (!path.EndsWith(".xnb"))
                path += ".xnb";

            // get file
            return new FileInfo(path).Exists;
        }

        /// <summary>Get the asset key for a tilesheet in the game's <c>Maps</c> content folder.</summary>
        /// <param name="relativePath">The tilesheet image source.</param>
        private string GetContentKeyForTilesheetImageSource(string relativePath)
        {
            string key = relativePath;
            string topFolder = PathUtilities.GetSegments(key, limit: 2)[0];

            // convert image source relative to map file into asset key
            if (!topFolder.Equals("Maps", StringComparison.OrdinalIgnoreCase))
                key = Path.Combine("Maps", key);

            // remove file extension from unpacked file
            if (key.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                key = key.Substring(0, key.Length - 4);

            return key;
        }
    }
}
