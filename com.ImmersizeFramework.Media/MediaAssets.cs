using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace com.ImmersizeFramework.Media {
    public interface IMediaAsset : IDisposable {
        string ID { get; }
        string Path { get; }
        MediaManager.MediaType Type { get; }
        MediaManager.ProcessingState State { get; set; }
        DateTime LastAccessed { get; set; }
        bool IsLocked { get; set; }
        long EstimatedSizeBytes { get; }
        UnityEngine.Object Asset { get; }
    }

    public abstract class MediaAsset<T> : IMediaAsset where T : UnityEngine.Object {
        public string ID { get; protected set; }
        public string Path { get; protected set; }
        public MediaManager.MediaType Type { get; protected set; }
        public MediaManager.ProcessingState State { get; set; }
        public DateTime LastAccessed { get; set; } = DateTime.UtcNow;
        public bool IsLocked { get; set; }
        public abstract long EstimatedSizeBytes { get; }
        public T TypedAsset { get; protected set; }
        public UnityEngine.Object Asset => TypedAsset;

        protected MediaAsset(string id, string path, MediaManager.MediaType type) {
            ID = id; Path = path; Type = type;
            State = MediaManager.ProcessingState.Pending;
        }

        public virtual void Dispose() {
            if (TypedAsset != null) {
                UnityEngine.Object.DestroyImmediate(TypedAsset);
                TypedAsset = null;
            }
            State = MediaManager.ProcessingState.Expired;
        }
    }

    public sealed class ImageAsset : MediaAsset<Texture2D> {
        public override long EstimatedSizeBytes => TypedAsset != null 
            ? TypedAsset.width * TypedAsset.height * 4 
            : 0;

        public ImageAsset(string id, string path, Texture2D texture) : base(id, path, MediaManager.MediaType.Image) {
            TypedAsset = texture;
            State = MediaManager.ProcessingState.Cached;
        }
    }

    public sealed class VideoAsset : MediaAsset<UnityEngine.Video.VideoClip> {
        public string ExternalUrl { get; set; }
        public bool IsExternalVideo => !string.IsNullOrEmpty(ExternalUrl);
        
        public override long EstimatedSizeBytes => TypedAsset != null 
            ? (long)(TypedAsset.length * TypedAsset.frameRate * TypedAsset.width * TypedAsset.height * 3)
            : IsExternalVideo ? 1024 * 1024 : 0;

        public VideoAsset(string id, string path, UnityEngine.Video.VideoClip clip) : base(id, path, MediaManager.MediaType.Video) {
            TypedAsset = clip;
            State = MediaManager.ProcessingState.Cached;
        }
    }

    public sealed class AudioAsset : MediaAsset<AudioClip> {
        public override long EstimatedSizeBytes => TypedAsset != null 
            ? TypedAsset.samples * TypedAsset.channels * sizeof(float)
            : 0;

        public AudioAsset(string id, string path, AudioClip clip) : base(id, path, MediaManager.MediaType.Audio) {
            TypedAsset = clip;
            State = MediaManager.ProcessingState.Cached;
        }
    }

    public sealed class ModelAsset : MediaAsset<GameObject> {
        public override long EstimatedSizeBytes => 1024 * 1024;

        public ModelAsset(string id, string path, GameObject model) : base(id, path, MediaManager.MediaType.Model) {
            TypedAsset = model;
            State = MediaManager.ProcessingState.Cached;
        }
    }

    public sealed class AnimationAsset : MediaAsset<AnimationClip> {
        public override long EstimatedSizeBytes => TypedAsset != null 
            ? (long)(TypedAsset.length * TypedAsset.frameRate * 1024)
            : 0;

        public AnimationAsset(string id, string path, AnimationClip clip) : base(id, path, MediaManager.MediaType.Animation) {
            TypedAsset = clip;
            State = MediaManager.ProcessingState.Cached;
        }
    }

    public sealed class ShaderAsset : MediaAsset<Shader> {
        public override long EstimatedSizeBytes => 64 * 1024;

        public ShaderAsset(string id, string path, Shader shader) : base(id, path, MediaManager.MediaType.Shader) {
            TypedAsset = shader;
            State = MediaManager.ProcessingState.Cached;
        }
    }

    public sealed class MaterialAsset : MediaAsset<Material> {
        public override long EstimatedSizeBytes => 16 * 1024;

        public MaterialAsset(string id, string path, Material material) : base(id, path, MediaManager.MediaType.Material) {
            TypedAsset = material;
            State = MediaManager.ProcessingState.Cached;
        }
    }

    public sealed class FontAsset : MediaAsset<Font> {
        public override long EstimatedSizeBytes => 512 * 1024;

        public FontAsset(string id, string path, Font font) : base(id, path, MediaManager.MediaType.Font) {
            TypedAsset = font;
            State = MediaManager.ProcessingState.Cached;
        }
    }

    public sealed class UIAsset : MediaAsset<GameObject> {
        public override long EstimatedSizeBytes => 256 * 1024;

        public UIAsset(string id, string path, GameObject prefab) : base(id, path, MediaManager.MediaType.UI) {
            TypedAsset = prefab;
            State = MediaManager.ProcessingState.Cached;
        }
    }
}
