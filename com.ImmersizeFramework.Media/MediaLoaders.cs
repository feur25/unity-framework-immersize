using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using System.Runtime.CompilerServices;

namespace com.ImmersizeFramework.Media {
    public interface IMediaLoader {
        Task<IMediaAsset> LoadAsync(MediaManager.MediaRequest request, CancellationToken token);
        bool CanLoad(string extension);
        MediaManager.MediaType SupportedType { get; }
    }

    public abstract class MediaLoader<T> : IMediaLoader where T : UnityEngine.Object {
        protected readonly MediaManager.MediaSettings Settings;
        public abstract MediaManager.MediaType SupportedType { get; }

        protected MediaLoader(MediaManager.MediaSettings settings) => Settings = settings;

        public abstract Task<IMediaAsset> LoadAsync(MediaManager.MediaRequest request, CancellationToken token);
        public abstract bool CanLoad(string extension);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected async Task<byte[]> LoadFileBytes(string path, CancellationToken token) {
            if (path.StartsWith("http")) {
                using var request = UnityWebRequest.Get(path);
                var operation = request.SendWebRequest();
                
                while (!operation.isDone && !token.IsCancellationRequested)
                    await Task.Yield();
                
                token.ThrowIfCancellationRequested();
                
                if (request.result == UnityWebRequest.Result.Success)
                    return request.downloadHandler.data;
                
                throw new Exception($"Failed to download: {request.error}");
            }
            
            return await File.ReadAllBytesAsync(path, token);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected async Task<T> LoadUnityAsset<TAsset>(string path, CancellationToken token) where TAsset : T {
            if (Application.isPlaying) {
                var request = Resources.LoadAsync<TAsset>(path);

                for (; !request.isDone && !token.IsCancellationRequested ; await Task.Yield());

                token.ThrowIfCancellationRequested();
                return request.asset as T;
            }
            
            return Resources.Load<TAsset>(path) as T;
        }
    }

    public sealed class ImageLoader : MediaLoader<Texture2D> {
        public override MediaManager.MediaType SupportedType => MediaManager.MediaType.Image;

        public ImageLoader(MediaManager.MediaSettings settings) : base(settings) { }

        public override bool CanLoad(string extension) => extension.ToLowerInvariant() switch {
            ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".gif" => true,
            _ => false
        };

        public override async Task<IMediaAsset> LoadAsync(MediaManager.MediaRequest request, CancellationToken token) {
            try {
                var bytes = await LoadFileBytes(request.Path, token);
                var texture = new Texture2D(2, 2);
                
                if (texture.LoadImage(bytes)) {
                    if (Settings.AutoOptimize) texture = await OptimizeTexture(texture, token);
                    return new ImageAsset(request.ID, request.Path, texture);
                }
                
                throw new Exception("Failed to load image data");
            } catch (Exception ex) {
                throw new Exception($"Image loading failed: {ex.Message}", ex);
            }
        }

        private async Task<Texture2D> OptimizeTexture(Texture2D source, CancellationToken token) {
            return await Task.Run(() => {
                var maxSize = Settings.TextureMaxSize;
                
                if (source.width > maxSize || source.height > maxSize) {
                    var scale = Mathf.Min((float)maxSize / source.width, (float)maxSize / source.height);
                    var newWidth = Mathf.RoundToInt(source.width * scale);
                    var newHeight = Mathf.RoundToInt(source.height * scale);
                    
                    var resized = new Texture2D(newWidth, newHeight, source.format, Settings.EnableMipMaps);
                    var pixels = source.GetPixels();
                    var resizedPixels = new Color[newWidth * newHeight];
                    
                    for (int y = 0; y < newHeight; y++) {
                        for (int x = 0; x < newWidth; x++) {
                            var srcX = Mathf.RoundToInt(x / scale);
                            var srcY = Mathf.RoundToInt(y / scale);
                            resizedPixels[y * newWidth + x] = pixels[srcY * source.width + srcX];
                        }
                    }
                    
                    resized.SetPixels(resizedPixels);
                    resized.Apply();
                    
                    UnityEngine.Object.DestroyImmediate(source);
                    return resized;
                }
                
                return source;
            }, token);
        }
    }

    public sealed class VideoLoader : MediaLoader<UnityEngine.Video.VideoClip> {
        public override MediaManager.MediaType SupportedType => MediaManager.MediaType.Video;

        public VideoLoader(MediaManager.MediaSettings settings) : base(settings) { }

        public override bool CanLoad(string extension) => extension.ToLowerInvariant() switch {
            ".mp4" or ".avi" or ".mov" or ".webm" or ".mkv" => true,
            _ => false
        };

        public override async Task<IMediaAsset> LoadAsync(MediaManager.MediaRequest request, CancellationToken token) {
            try {
                if (request.Path.StartsWith("http")) {
                    return new VideoAsset(request.ID, request.Path, null) { ExternalUrl = request.Path };
                }
                
                var videoClip = await LoadUnityAsset<UnityEngine.Video.VideoClip>(request.Path, token);
                if (videoClip != null) return new VideoAsset(request.ID, request.Path, videoClip);
                
                throw new Exception("Failed to load video clip");
            } catch (Exception ex) {
                throw new Exception($"Video loading failed: {ex.Message}", ex);
            }
        }
    }

    public sealed class AudioLoader : MediaLoader<AudioClip> {
        public override MediaManager.MediaType SupportedType => MediaManager.MediaType.Audio;

        public AudioLoader(MediaManager.MediaSettings settings) : base(settings) { }

        public override bool CanLoad(string extension) => extension.ToLowerInvariant() switch {
            ".mp3" or ".wav" or ".ogg" or ".aac" or ".flac" => true,
            _ => false
        };

        public override async Task<IMediaAsset> LoadAsync(MediaManager.MediaRequest request, CancellationToken token) {
            try {
                AudioClip clip;
                
                if (request.Path.StartsWith("http")) {
                    var audioType = GetAudioType(Path.GetExtension(request.Path));
                    using var webRequest = UnityWebRequestMultimedia.GetAudioClip(request.Path, audioType);
                    
                    var operation = webRequest.SendWebRequest();
                    while (!operation.isDone && !token.IsCancellationRequested)
                        await Task.Yield();
                    
                    token.ThrowIfCancellationRequested();
                    
                    if (webRequest.result == UnityWebRequest.Result.Success) {
                        clip = DownloadHandlerAudioClip.GetContent(webRequest);
                    } else {
                        throw new Exception($"Failed to download audio: {webRequest.error}");
                    }
                } else {
                    clip = await LoadUnityAsset<AudioClip>(request.Path, token);
                }
                
                if (clip != null) return new AudioAsset(request.ID, request.Path, clip);
                
                throw new Exception("Failed to load audio clip");
            } catch (Exception ex) {
                throw new Exception($"Audio loading failed: {ex.Message}", ex);
            }
        }

        private AudioType GetAudioType(string extension) => extension.ToLowerInvariant() switch {
            ".mp3" => AudioType.MPEG,
            ".wav" => AudioType.WAV,
            ".ogg" => AudioType.OGGVORBIS,
            ".aac" => AudioType.ACC,
            _ => AudioType.UNKNOWN
        };
    }

    public sealed class ModelLoader : MediaLoader<GameObject> {
        public override MediaManager.MediaType SupportedType => MediaManager.MediaType.Model;

        public ModelLoader(MediaManager.MediaSettings settings) : base(settings) { }

        public override bool CanLoad(string extension) => extension.ToLowerInvariant() switch {
            ".fbx" or ".obj" or ".dae" or ".blend" or ".3ds" => true,
            _ => false
        };

        public override async Task<IMediaAsset> LoadAsync(MediaManager.MediaRequest request, CancellationToken token) {
            try {
                var model = await LoadUnityAsset<GameObject>(request.Path, token);
                if (model != null) return new ModelAsset(request.ID, request.Path, model);
                
                throw new Exception("Failed to load 3D model");
            } catch (Exception ex) {
                throw new Exception($"Model loading failed: {ex.Message}", ex);
            }
        }
    }

    public sealed class AnimationLoader : MediaLoader<AnimationClip> {
        public override MediaManager.MediaType SupportedType => MediaManager.MediaType.Animation;

        public AnimationLoader(MediaManager.MediaSettings settings) : base(settings) { }

        public override bool CanLoad(string extension) => extension.ToLowerInvariant() switch {
            ".anim" or ".controller" or ".mask" => true,
            _ => false
        };

        public override async Task<IMediaAsset> LoadAsync(MediaManager.MediaRequest request, CancellationToken token) {
            try {
                var clip = await LoadUnityAsset<AnimationClip>(request.Path, token);
                if (clip != null) return new AnimationAsset(request.ID, request.Path, clip);
                
                throw new Exception("Failed to load animation clip");
            } catch (Exception ex) {
                throw new Exception($"Animation loading failed: {ex.Message}", ex);
            }
        }
    }

    public sealed class ShaderLoader : MediaLoader<Shader> {
        public override MediaManager.MediaType SupportedType => MediaManager.MediaType.Shader;

        public ShaderLoader(MediaManager.MediaSettings settings) : base(settings) { }

        public override bool CanLoad(string extension) => extension.ToLowerInvariant() switch {
            ".shader" or ".hlsl" or ".glsl" => true,
            _ => false
        };

        public override async Task<IMediaAsset> LoadAsync(MediaManager.MediaRequest request, CancellationToken token) {
            try {
                var shader = await LoadUnityAsset<Shader>(request.Path, token);
                if (shader != null) return new ShaderAsset(request.ID, request.Path, shader);
                
                throw new Exception("Failed to load shader");
            } catch (Exception ex) {
                throw new Exception($"Shader loading failed: {ex.Message}", ex);
            }
        }
    }

    public sealed class MaterialLoader : MediaLoader<Material> {
        public override MediaManager.MediaType SupportedType => MediaManager.MediaType.Material;

        public MaterialLoader(MediaManager.MediaSettings settings) : base(settings) { }

        public override bool CanLoad(string extension) => extension.ToLowerInvariant() switch {
            ".mat" or ".material" => true,
            _ => false
        };

        public override async Task<IMediaAsset> LoadAsync(MediaManager.MediaRequest request, CancellationToken token) {
            try {
                var material = await LoadUnityAsset<Material>(request.Path, token);
                if (material != null) return new MaterialAsset(request.ID, request.Path, material);
                
                throw new Exception("Failed to load material");
            } catch (Exception ex) {
                throw new Exception($"Material loading failed: {ex.Message}", ex);
            }
        }
    }

    public sealed class FontLoader : MediaLoader<Font> {
        public override MediaManager.MediaType SupportedType => MediaManager.MediaType.Font;

        public FontLoader(MediaManager.MediaSettings settings) : base(settings) { }

        public override bool CanLoad(string extension) => extension.ToLowerInvariant() switch {
            ".ttf" or ".otf" or ".fnt" => true,
            _ => false
        };

        public override async Task<IMediaAsset> LoadAsync(MediaManager.MediaRequest request, CancellationToken token) {
            try {
                var font = await LoadUnityAsset<Font>(request.Path, token);
                if (font != null) return new FontAsset(request.ID, request.Path, font);
                
                throw new Exception("Failed to load font");
            } catch (Exception ex) {
                throw new Exception($"Font loading failed: {ex.Message}", ex);
            }
        }
    }

    public sealed class UILoader : MediaLoader<GameObject> {
        public override MediaManager.MediaType SupportedType => MediaManager.MediaType.UI;

        public UILoader(MediaManager.MediaSettings settings) : base(settings) { }

        public override bool CanLoad(string extension) => extension.ToLowerInvariant() switch {
            ".prefab" or ".asset" => true,
            _ => false
        };

        public override async Task<IMediaAsset> LoadAsync(MediaManager.MediaRequest request, CancellationToken token) {
            try {
                var prefab = await LoadUnityAsset<GameObject>(request.Path, token);
                if (prefab != null) return new UIAsset(request.ID, request.Path, prefab);
                
                throw new Exception("Failed to load UI prefab");
            } catch (Exception ex) {
                throw new Exception($"UI loading failed: {ex.Message}", ex);
            }
        }
    }
}
