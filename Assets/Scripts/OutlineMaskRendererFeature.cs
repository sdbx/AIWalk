using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

public class OutlineMaskRendererFeature : ScriptableRendererFeature
{
  private static readonly int OutlineMaskID = Shader.PropertyToID("_OutlineMask");

  [System.Serializable]
  public class Settings
  {
    [Header("Outline Mask")]
    public LayerMask outlineLayer;
    public Material maskMaterial;

    [Header("Outline Composite")]
    public Material outlineMaterial;
  }

  public Settings settings = new();

  private OutlineMaskPass maskPass;
  private OutlineCompositePass compositePass;

  public override void Create()
  {
    maskPass = new OutlineMaskPass(
        settings.outlineLayer,
        settings.maskMaterial
    )
    {
      renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing
    };

    compositePass = new OutlineCompositePass(
        settings.outlineMaterial
    )
    {
      renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing
    };
  }

  public override void AddRenderPasses(
      ScriptableRenderer renderer,
      ref RenderingData renderingData)
  {
    if (settings.maskMaterial == null || settings.outlineMaterial == null)
      return;

    renderer.EnqueuePass(maskPass);
    renderer.EnqueuePass(compositePass);
  }

  protected override void Dispose(bool disposing)
  {
    maskPass?.Dispose();
    compositePass?.Dispose();
  }

  private sealed class OutlineMaskPass : ScriptableRenderPass
  {
    private readonly LayerMask outlineLayer;
    private readonly Material maskMaterial;

    private class PassData
    {
      public RendererListHandle rendererList;
    }

    public OutlineMaskPass(LayerMask outlineLayer, Material maskMaterial)
    {
      this.outlineLayer = outlineLayer;
      this.maskMaterial = maskMaterial;
    }

    public override void RecordRenderGraph(
        RenderGraph renderGraph,
        ContextContainer frameContext)
    {
      if (maskMaterial == null)
        return;

      var renderingData = frameContext.Get<UniversalRenderingData>();
      var cameraData = frameContext.Get<UniversalCameraData>();
      var lightData = frameContext.Get<UniversalLightData>();
      var resourceData = frameContext.Get<UniversalResourceData>();

      var maskTexture = CreateMaskTexture(
          renderGraph,
          cameraData
      );

      var rendererList = CreateRendererList(
          renderGraph,
          renderingData,
          cameraData,
          lightData
      );

      using var builder = renderGraph.AddRasterRenderPass<PassData>(
          "Outline Mask",
          out var passData
      );

      passData.rendererList = rendererList;

      builder.UseRendererList(rendererList);
      builder.SetRenderAttachment(
          maskTexture,
          0,
          AccessFlags.Write
      );

      if (resourceData.activeDepthTexture.IsValid())
      {
        builder.SetRenderAttachmentDepth(
            resourceData.activeDepthTexture,
            AccessFlags.Read
        );
      }

      builder.SetGlobalTextureAfterPass(
          maskTexture,
          OutlineMaskID
      );

      builder.SetRenderFunc(
          static (PassData data, RasterGraphContext context) =>
          {
            context.cmd.ClearRenderTarget(
                      clearDepth: false,
                      clearColor: true,
                      backgroundColor: Color.black
                  );

            context.cmd.DrawRendererList(data.rendererList);
          }
      );
    }

    private TextureHandle CreateMaskTexture(
        RenderGraph renderGraph,
        UniversalCameraData cameraData)
    {
      var descriptor = cameraData.cameraTargetDescriptor;

      descriptor.depthBufferBits = 0;
      descriptor.msaaSamples = 1;
      descriptor.colorFormat = RenderTextureFormat.R8;

      return UniversalRenderer.CreateRenderGraphTexture(
          renderGraph,
          descriptor,
          "_OutlineMask",
          false
      );
    }

    private RendererListHandle CreateRendererList(
        RenderGraph renderGraph,
        UniversalRenderingData renderingData,
        UniversalCameraData cameraData,
        UniversalLightData lightData)
    {
      var filteringSettings = new FilteringSettings(
          RenderQueueRange.all,
          outlineLayer
      );

      var drawingSettings = RenderingUtils.CreateDrawingSettings(
          new ShaderTagId("UniversalForward"),
          renderingData,
          cameraData,
          lightData,
          cameraData.defaultOpaqueSortFlags
      );

      drawingSettings.overrideMaterial = maskMaterial;
      drawingSettings.overrideMaterialPassIndex = 0;

      var rendererListParams = new RendererListParams(
          renderingData.cullResults,
          drawingSettings,
          filteringSettings
      );

      return renderGraph.CreateRendererList(rendererListParams);
    }

    public void Dispose()
    {
    }
  }

  private sealed class OutlineCompositePass : ScriptableRenderPass
  {
    private readonly Material outlineMaterial;

    private class CopyPassData
    {
      public TextureHandle source;
    }

    private class CompositePassData
    {
      public TextureHandle source;
      public Material material;
    }

    public OutlineCompositePass(Material outlineMaterial)
    {
      this.outlineMaterial = outlineMaterial;
    }

    public override void RecordRenderGraph(
        RenderGraph renderGraph,
        ContextContainer frameContext)
    {
      if (outlineMaterial == null)
        return;

      var resourceData = frameContext.Get<UniversalResourceData>();

      if (resourceData.isActiveTargetBackBuffer)
        return;

      var cameraColor = resourceData.activeColorTexture;
      var copiedColor = CopyCameraColor(
          renderGraph,
          cameraColor
      );

      Composite(
          renderGraph,
          cameraColor,
          copiedColor
      );
    }

    private TextureHandle CopyCameraColor(
        RenderGraph renderGraph,
        TextureHandle source)
    {
      var descriptor = renderGraph.GetTextureDesc(source);

      descriptor.name = "_OutlineCameraColorCopy";
      descriptor.clearBuffer = false;
      descriptor.depthBufferBits = 0;

      var destination = renderGraph.CreateTexture(descriptor);

      using var builder = renderGraph.AddRasterRenderPass<CopyPassData>(
          "Outline - Copy Camera Color",
          out var passData
      );

      passData.source = source;

      builder.UseTexture(
          source,
          AccessFlags.Read
      );

      builder.SetRenderAttachment(
          destination,
          0,
          AccessFlags.Write
      );

      builder.SetRenderFunc(
          static (CopyPassData data, RasterGraphContext context) =>
          {
            Blitter.BlitTexture(
                      context.cmd,
                      data.source,
                      new Vector4(1f, 1f, 0f, 0f),
                      0f,
                      false
                  );
          }
      );

      return destination;
    }

    private void Composite(
        RenderGraph renderGraph,
        TextureHandle destination,
        TextureHandle source)
    {
      using var builder = renderGraph.AddRasterRenderPass<CompositePassData>(
          "Outline Composite",
          out var passData
      );

      passData.source = source;
      passData.material = outlineMaterial;

      builder.UseTexture(
          source,
          AccessFlags.Read
      );

      builder.UseGlobalTexture(
          OutlineMaskID,
          AccessFlags.Read
      );

      builder.SetRenderAttachment(
          destination,
          0,
          AccessFlags.Write
      );

      builder.SetRenderFunc(
          static (CompositePassData data, RasterGraphContext context) =>
          {
            Blitter.BlitTexture(
                      context.cmd,
                      data.source,
                      new Vector4(1f, 1f, 0f, 0f),
                      data.material,
                      0
                  );
          }
      );
    }

    public void Dispose()
    {
    }
  }
}