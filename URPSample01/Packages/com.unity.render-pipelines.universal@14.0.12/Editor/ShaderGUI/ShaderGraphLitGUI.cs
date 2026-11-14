using System;
using UnityEditor.Rendering.Universal;
using UnityEditor.Rendering.Universal.ShaderGUI;
using UnityEngine;
using static Unity.Rendering.Universal.ShaderUtils;

namespace UnityEditor
{
    // Used for ShaderGraph Lit shaders
    class ShaderGraphLitGUI : BaseShaderGUI
    {
        public MaterialProperty workflowMode;

        MaterialProperty transparentSSRReceive;
        MaterialProperty refractionModel;
        MaterialProperty ior;
        MaterialProperty thickness;
        MaterialProperty transmittanceColor;
        MaterialProperty transmittanceDistance;

        MaterialProperty[] properties;

        // collect properties from the material properties
        public override void FindProperties(MaterialProperty[] properties)
        {
            // save off the list of all properties for shadergraph
            this.properties = properties;

            var material = materialEditor?.target as Material;
            if (material == null)
                return;

            base.FindProperties(properties);
            workflowMode = BaseShaderGUI.FindProperty(Property.SpecularWorkflowMode, properties, false);
            transparentSSRReceive = BaseShaderGUI.FindProperty(Property.TransparentSSRReceive, properties, false);
            refractionModel = BaseShaderGUI.FindProperty(Property.RefractionModel, properties, false);
            ior = BaseShaderGUI.FindProperty(Property.Ior, properties, false);
            thickness = BaseShaderGUI.FindProperty(Property.Thickness, properties, false);
            transmittanceColor = BaseShaderGUI.FindProperty(Property.TransmittanceColor, properties, false);
            transmittanceDistance = BaseShaderGUI.FindProperty(Property.TransmittanceDistance, properties, false);
        }

        public static void UpdateMaterial(Material material, MaterialUpdateType updateType)
        {
            // newly created materials should initialize the globalIlluminationFlags (default is off)
            if (updateType == MaterialUpdateType.CreatedNewMaterial)
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;

            bool automaticRenderQueue = GetAutomaticQueueControlSetting(material);
            BaseShaderGUI.UpdateMaterialSurfaceOptions(material, automaticRenderQueue);
            LitGUI.SetupSpecularWorkflowKeyword(material, out bool isSpecularWorkflow);
            LitGUI.SetScreenSpaceKeywords(material);
        }

        public override void ValidateMaterial(Material material)
        {
            if (material == null)
                throw new ArgumentNullException("material");

            UpdateMaterial(material, MaterialUpdateType.ModifiedMaterial);
        }

        public override void DrawSurfaceOptions(Material material)
        {
            if (material == null)
                throw new ArgumentNullException("material");

            // Use default labelWidth
            EditorGUIUtility.labelWidth = 0f;

            // Detect any changes to the material
            if (workflowMode != null)
                DoPopup(LitGUI.Styles.workflowModeText, workflowMode, Enum.GetNames(typeof(LitGUI.WorkflowMode)));
            base.DrawSurfaceOptions(material);

            // The screen space lighting material contract is shared with the built-in Lit shaders.
            // These properties are added by the Lit target, so the graph property drawer does not
            // show them; the surface options section owns them instead.
            bool opaque = surfaceTypeProp == null ||
                          (BaseShaderGUI.SurfaceType)surfaceTypeProp.floatValue == BaseShaderGUI.SurfaceType.Opaque;
            if (!opaque && transparentSSRReceive != null)
                DrawFloatToggleProperty(LitGUI.Styles.transparentSSRReceiveText, transparentSSRReceive, 1);

            if (!opaque && refractionModel != null)
            {
                DoPopup(LitGUI.Styles.refractionModelText, refractionModel, LitGUI.Styles.refractionModelNames);
                bool refracted = refractionModel.floatValue > 0.5f;
                EditorGUI.BeginDisabledGroup(!refracted);
                {
                    EditorGUI.indentLevel += 2;
                    if (ior != null)
                        materialEditor.ShaderProperty(ior, "Index Of Refraction");
                    if (thickness != null)
                        materialEditor.ShaderProperty(thickness, "Thickness");
                    if (transmittanceColor != null)
                        materialEditor.ShaderProperty(transmittanceColor, "Transmittance Color");
                    if (transmittanceDistance != null)
                        materialEditor.ShaderProperty(transmittanceDistance, "Transmittance Absorption Distance");
                    EditorGUI.indentLevel -= 2;
                }
                EditorGUI.EndDisabledGroup();
            }
        }

        // material main surface inputs
        public override void DrawSurfaceInputs(Material material)
        {
            DrawShaderGraphProperties(material, properties);
        }

        public override void DrawAdvancedOptions(Material material)
        {
            // Always show the queue control field.  Only show the render queue field if queue control is set to user override
            DoPopup(Styles.queueControl, queueControlProp, Styles.queueControlNames);
            if (material.HasProperty(Property.QueueControl) && material.GetFloat(Property.QueueControl) == (float)QueueControl.UserOverride)
                materialEditor.RenderQueueField();
            base.DrawAdvancedOptions(material);

            // ignore emission color for shadergraphs, because shadergraphs don't have a hard-coded emission property, it's up to the user
            materialEditor.DoubleSidedGIField();
            materialEditor.LightmapEmissionFlagsProperty(0, enabled: true, ignoreEmissionColor: true);
        }
    }
} // namespace UnityEditor
