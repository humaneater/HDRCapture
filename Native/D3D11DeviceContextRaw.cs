using System.Runtime.InteropServices;

namespace HdrCapture.Native;

[ComImport]
[Guid("C0BFA96C-E089-44FB-8EAF-26F8796190DA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ID3D11DeviceContextRaw
{
    void GetDevice(out nint device);
    [PreserveSig] int GetPrivateData(ref Guid guid, ref uint dataSize, nint data);
    [PreserveSig] int SetPrivateData(ref Guid guid, uint dataSize, nint data);
    [PreserveSig] int SetPrivateDataInterface(ref Guid guid, nint data);
    void VSSetConstantBuffers(uint startSlot, uint numBuffers, nint constantBuffers);
    void PSSetShaderResources(uint startSlot, uint numViews, nint shaderResourceViews);
    void PSSetShader(nint pixelShader, nint classInstances, uint numClassInstances);
    void PSSetSamplers(uint startSlot, uint numSamplers, nint samplers);
    void VSSetShader(nint vertexShader, nint classInstances, uint numClassInstances);
    void DrawIndexed(uint indexCount, uint startIndexLocation, int baseVertexLocation);
    void Draw(uint vertexCount, uint startVertexLocation);
    [PreserveSig] int Map(
        nint resource,
        uint subresource,
        uint mapType,
        uint mapFlags,
        out WinRtCaptureNative.D3D11MappedSubresource mapped);
    void Unmap(nint resource, uint subresource);
    void PSSetConstantBuffers(uint startSlot, uint numBuffers, nint constantBuffers);
    void IASetInputLayout(nint inputLayout);
    void IASetVertexBuffers(uint startSlot, uint numBuffers, nint vertexBuffers, nint strides, nint offsets);
    void IASetIndexBuffer(nint indexBuffer, uint format, uint offset);
    void DrawIndexedInstanced(uint indexCountPerInstance, uint instanceCount, uint startIndexLocation, int baseVertexLocation, uint startInstanceLocation);
    void DrawInstanced(uint vertexCountPerInstance, uint instanceCount, uint startVertexLocation, uint startInstanceLocation);
    void GSSetConstantBuffers(uint startSlot, uint numBuffers, nint constantBuffers);
    void GSSetShader(nint shader, nint classInstances, uint numClassInstances);
    void IASetPrimitiveTopology(uint topology);
    void VSSetShaderResources(uint startSlot, uint numViews, nint shaderResourceViews);
    void VSSetSamplers(uint startSlot, uint numSamplers, nint samplers);
    void Begin(nint async);
    void End(nint async);
    [PreserveSig] int GetData(nint async, nint data, uint dataSize, uint getDataFlags);
    void SetPredication(nint predicate, byte predicateValue);
    void GSSetShaderResources(uint startSlot, uint numViews, nint shaderResourceViews);
    void GSSetSamplers(uint startSlot, uint numSamplers, nint samplers);
    void OMSetRenderTargets(uint numViews, nint renderTargetViews, nint depthStencilView);
    void OMSetRenderTargetsAndUnorderedAccessViews(
        uint numRtv,
        nint rtvs,
        nint depthStencilView,
        uint uavStartSlot,
        uint numUavs,
        nint uavs,
        nint uavInitialCounts);
    void OMSetBlendState(nint blendState, nint blendFactor, uint sampleMask);
    void OMSetDepthStencilState(nint depthStencilState, uint stencilRef);
    void SOSetTargets(uint numBuffers, nint targets, nint offsets);
    void DrawAuto();
    void DrawIndexedInstancedIndirect(nint bufferForArgs, uint alignedByteOffsetForArgs);
    void DrawInstancedIndirect(nint bufferForArgs, uint alignedByteOffsetForArgs);
    void Dispatch(uint threadGroupCountX, uint threadGroupCountY, uint threadGroupCountZ);
    void DispatchIndirect(nint bufferForArgs, uint alignedByteOffsetForArgs);
    void RSSetState(nint rasterizerState);
    void RSSetViewports(uint numViewports, nint viewports);
    void RSSetScissorRects(uint numRects, nint rects);
    void CopySubresourceRegion(nint destinationResource, uint destinationSubresource, uint destinationX, uint destinationY, uint destinationZ, nint sourceResource, uint sourceSubresource, nint sourceBox);
    void CopyResource(nint destinationResource, nint sourceResource);
}
