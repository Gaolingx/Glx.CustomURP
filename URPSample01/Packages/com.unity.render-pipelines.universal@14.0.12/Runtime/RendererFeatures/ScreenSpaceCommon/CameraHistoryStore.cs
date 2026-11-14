using System;
using System.Collections.Generic;

namespace UnityEngine.Rendering.Universal
{
    internal interface IScreenSpaceHistoryEntry : IDisposable
    {
        // Called when the entry is created, resized or invalidated by a camera reset.
        void Reset(int width, int height, int frameIndex);

        // Called after a frame wrote the current buffer so the next frame can read it as history.
        void SwapBuffers();
    }

    // Per-camera (and per-XR-eye) history ownership. Effect parameters stay in the owning feature;
    // this store only tracks lifetime, size and reset generation.
    internal sealed class CameraHistoryStore<T> : IDisposable where T : class, IScreenSpaceHistoryEntry, new()
    {
        readonly struct Key : IEquatable<Key>
        {
            readonly int m_CameraInstanceId;
            readonly int m_XrEye;

            public Key(int cameraInstanceId, int xrEye)
            {
                m_CameraInstanceId = cameraInstanceId;
                m_XrEye = xrEye;
            }

            public bool Equals(Key other)
            {
                return m_CameraInstanceId == other.m_CameraInstanceId && m_XrEye == other.m_XrEye;
            }

            public override bool Equals(object obj)
            {
                return obj is Key other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (m_CameraInstanceId * 397) ^ m_XrEye;
                }
            }
        }

        sealed class Entry
        {
            public Camera camera;
            public T history;
            public int width;
            public int height;
            public int lastUsedFrame;
            public int resetToken;
        }

        readonly Dictionary<Key, Entry> m_Entries = new Dictionary<Key, Entry>();
        readonly List<Key> m_KeysToRemove = new List<Key>();
        bool m_Disposed;

        internal int entryCount => m_Entries.Count;

        internal T GetOrCreate(Camera camera, int xrEye, int width, int height, int frameIndex, int resetToken, out bool needsReset)
        {
            if (m_Disposed)
                throw new ObjectDisposedException(nameof(CameraHistoryStore<T>));
            if (camera == null)
                throw new ArgumentNullException(nameof(camera));

            var key = new Key(camera.GetInstanceID(), xrEye);
            if (m_Entries.TryGetValue(key, out var entry) && entry.camera != null)
            {
                bool resized = entry.width != width || entry.height != height;
                bool tokenChanged = entry.resetToken != resetToken;
                // Negative tokens mean "hold the reset" (XR multipass/second eye and TAA debug hold).
                bool heldReset = resetToken < 0;

                entry.width = width;
                entry.height = height;
                entry.lastUsedFrame = frameIndex;
                entry.resetToken = resetToken;

                needsReset = resized || tokenChanged || heldReset;
                if (needsReset)
                    entry.history.Reset(width, height, frameIndex);

                return entry.history;
            }

            // A destroyed camera can share its instance id with a newly created one.
            if (entry != null)
            {
                entry.history.Dispose();
                m_Entries.Remove(key);
            }

            var history = new T();
            history.Reset(width, height, frameIndex);
            m_Entries.Add(key, new Entry
            {
                camera = camera,
                history = history,
                width = width,
                height = height,
                lastUsedFrame = frameIndex,
                resetToken = resetToken,
            });
            needsReset = true;
            return history;
        }

        internal void PruneDestroyedCameras()
        {
            m_KeysToRemove.Clear();
            foreach (var pair in m_Entries)
            {
                if (pair.Value.camera == null)
                    m_KeysToRemove.Add(pair.Key);
            }

            DisposeKeys();
        }

        // Called after the current camera acquired its entry so an idle camera ages out instead of leaking.
        internal void PruneUnused(int frameIndex, int maxUnusedFrames)
        {
            if (maxUnusedFrames < 0)
                return;

            m_KeysToRemove.Clear();
            foreach (var pair in m_Entries)
            {
                uint age = unchecked((uint)(frameIndex - pair.Value.lastUsedFrame));
                if (age > (uint)maxUnusedFrames)
                    m_KeysToRemove.Add(pair.Key);
            }

            DisposeKeys();
        }

        internal void TrimToCount(int maxCachedCameras)
        {
            while (m_Entries.Count > Mathf.Max(0, maxCachedCameras))
            {
                bool hasOldest = false;
                Key oldestKey = default;
                int oldestFrame = 0;
                foreach (var pair in m_Entries)
                {
                    if (!hasOldest || pair.Value.lastUsedFrame < oldestFrame)
                    {
                        hasOldest = true;
                        oldestFrame = pair.Value.lastUsedFrame;
                        oldestKey = pair.Key;
                    }
                }

                if (!hasOldest)
                    break;

                DisposeKey(oldestKey);
            }
        }

        internal void Clear()
        {
            foreach (var pair in m_Entries)
                pair.Value.history.Dispose();

            m_Entries.Clear();
        }

        public void Dispose()
        {
            if (m_Disposed)
                return;

            Clear();
            m_Disposed = true;
        }

        void DisposeKeys()
        {
            foreach (var key in m_KeysToRemove)
                DisposeKey(key);

            m_KeysToRemove.Clear();
        }

        void DisposeKey(Key key)
        {
            if (!m_Entries.TryGetValue(key, out var entry))
                return;

            entry.history.Dispose();
            m_Entries.Remove(key);
        }
    }
}
