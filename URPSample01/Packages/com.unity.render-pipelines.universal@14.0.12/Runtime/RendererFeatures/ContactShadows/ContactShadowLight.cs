namespace UnityEngine.Rendering.Universal
{
    [AddComponentMenu("Rendering/Contact Shadow Light"), DisallowMultipleComponent, ExecuteAlways]
    public sealed class ContactShadowLight : MonoBehaviour
    {
        public bool castContactShadows = true;
        bool m_WarnedMissingLight;

        void OnEnable()
        {
            if (m_WarnedMissingLight || TryGetComponent<Light>(out _))
                return;

            m_WarnedMissingLight = true;
            Debug.LogWarning("Contact Shadow Light requires a Light on the same GameObject.", this);
        }

        internal static bool IsMarked(Light light) =>
            light != null && light.enabled &&
            (light.type == LightType.Directional || light.type == LightType.Point || light.type == LightType.Spot) &&
            light.TryGetComponent<ContactShadowLight>(out var marker) && marker.isActiveAndEnabled && marker.castContactShadows;
    }
}
