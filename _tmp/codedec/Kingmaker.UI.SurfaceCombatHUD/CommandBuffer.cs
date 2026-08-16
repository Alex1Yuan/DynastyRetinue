using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

namespace Kingmaker.UI.SurfaceCombatHUD;

[BurstCompile]
public struct CommandBuffer(Allocator allocator) : IDisposable
{
	internal NativeList<CommandRecord> recordList = new NativeList<CommandRecord>(allocator);

	internal NativeList<WriteFillCommandData> writeFillCommandDataList = new NativeList<WriteFillCommandData>(allocator);

	internal NativeList<BuildFillCommandData> buildFillCommandDataList = new NativeList<BuildFillCommandData>(allocator);

	internal NativeList<ComposeOutlineMeshCommandData> composeOutlineMeshCommandDataList = new NativeList<ComposeOutlineMeshCommandData>(allocator);

	internal NativeList<AppendMeshCommandData> appendMeshCommandDataList = new NativeList<AppendMeshCommandData>(allocator);

	public void CopyFrom(ref CommandBuffer src)
	{
		recordList.CopyFrom(src.recordList.AsArray());
		writeFillCommandDataList.CopyFrom(src.writeFillCommandDataList.AsArray());
		buildFillCommandDataList.CopyFrom(src.buildFillCommandDataList.AsArray());
		composeOutlineMeshCommandDataList.CopyFrom(src.composeOutlineMeshCommandDataList.AsArray());
		appendMeshCommandDataList.CopyFrom(src.appendMeshCommandDataList.AsArray());
	}

	public void Clear()
	{
		recordList.Clear();
		writeFillCommandDataList.Clear();
		buildFillCommandDataList.Clear();
		composeOutlineMeshCommandDataList.Clear();
		appendMeshCommandDataList.Clear();
	}

	public void Dispose()
	{
		recordList.Dispose();
		writeFillCommandDataList.Dispose();
		buildFillCommandDataList.Dispose();
		composeOutlineMeshCommandDataList.Dispose();
		appendMeshCommandDataList.Dispose();
	}

	public void WriteFill(int materialId, int shapeId, SurfaceCellFilterData selectFilter)
	{
		CommandRecord value = new CommandRecord
		{
			code = CommandCode.WriteFill,
			dataIndex = writeFillCommandDataList.Length
		};
		WriteFillCommandData value2 = new WriteFillCommandData
		{
			materialId = materialId,
			shapeId = shapeId,
			selectFilter = selectFilter
		};
		recordList.Add(in value);
		writeFillCommandDataList.Add(in value2);
	}

	public void ClearFill()
	{
		CommandRecord value = new CommandRecord
		{
			code = CommandCode.ClearFillBuffer,
			dataIndex = -1
		};
		recordList.Add(in value);
	}

	public void ClearOutline()
	{
		CommandRecord value = new CommandRecord
		{
			code = CommandCode.ClearOutlineBuffer,
			dataIndex = -1
		};
		recordList.Add(in value);
	}

	public void BuildFill(float3 meshOffset)
	{
		CommandRecord value = new CommandRecord
		{
			code = CommandCode.BuildFill,
			dataIndex = buildFillCommandDataList.Length
		};
		BuildFillCommandData value2 = new BuildFillCommandData
		{
			meshOffset = meshOffset
		};
		recordList.Add(in value);
		buildFillCommandDataList.Add(in value2);
	}

	public void ComposeOutlineMesh(OutlineType lineType, bool overwrite, float3 meshOffset, OutlineCellFilterData shape, OutlineCellFilterData mask)
	{
		CommandRecord value = new CommandRecord
		{
			code = CommandCode.ComposeOutlineMesh,
			dataIndex = composeOutlineMeshCommandDataList.Length
		};
		ComposeOutlineMeshCommandData value2 = new ComposeOutlineMeshCommandData
		{
			lineType = lineType,
			overwrite = overwrite,
			meshOffset = meshOffset,
			shape = shape,
			mask = mask
		};
		recordList.Add(in value);
		composeOutlineMeshCommandDataList.Add(in value2);
	}

	public void AppendOutlineMesh(int materialId)
	{
		CommandRecord value = new CommandRecord
		{
			code = CommandCode.AppendOutlineMesh,
			dataIndex = appendMeshCommandDataList.Length
		};
		AppendMeshCommandData value2 = new AppendMeshCommandData
		{
			materialId = materialId
		};
		recordList.Add(in value);
		appendMeshCommandDataList.Add(in value2);
	}
}
