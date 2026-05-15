using System;
using Godot;

namespace AntHill;

/// <summary>
/// 3D renderer for the "Worker" archetype: one MultiMeshInstance3D, identity per-instance transforms (sunk cost),
/// and a 16-byte-per-instance RGBA32F state texture refreshed each frame. Future archetypes (Soldier, Larva,
/// Spider, …) reuse the same texture layout and shader skeleton — see "State-Texture Layout v1.0" below.
///
/// The vertex shader (see <see cref="ShaderSource"/>) does the per-instance work:
///   • <c>floatBitsToUint(texelFetch(state_tex, INSTANCE_ID))</c> to recover the packed uints
///   • build the model matrix from <c>pos_xz</c> + <c>yaw</c> + heightmap-Y
///   • read <c>color_rgb</c> from <c>texel.b</c> and forward to fragment
///
/// Each worker thread of the simulation writes into its own RenderWorkerBuffer (12 floats per visible ant);
/// this renderer coalesces all worker buffers into the single state texture each frame.
///
/// ──────────────────────────────────────────────────────────────────────────────────────────────────────
///                       STATE-TEXTURE LAYOUT v1.0  —  FROZEN AT PHASE 2
/// ──────────────────────────────────────────────────────────────────────────────────────────────────────
/// Format: <c>Image.Format.Rgbaf</c> — 4 channels × 32 bits = 16 B / texel. Read as <c>uvec4</c> via
/// <c>floatBitsToUint(texelFetch(...))</c> in the vertex shader.
///
///   texel.r  (32 bits) =  pos_x_u16        (bits  0–15)  | pos_z_u16     (bits 16–31)
///                         fraction of WorldSizeM ×65535        same
///   texel.g  (32 bits) =  yaw_u16          (bits  0–15)  | flags_u16     (bits 16–31)
///                         angle in 1/65536 turn                state bits (carry/return/alarm/etc.)
///   texel.b  (32 bits) =  scale_byte       (bits  0–7)   | color_r       (8–15)
///                                                          | color_g     (16–23) | color_b (24–31)
///                         0..255 mapped per-shader scale_min..max
///   texel.a  (32 bits) =  alarm_pulse      (bits  0–7)   | carry_amount  (8–15)
///                         | species_id     (16–19)       | colony_id     (20–23)
///                         | caste_id       (24–27)       | health_norm   (28–31)
///                         reserved until Phase 5 — written as 0 today
///
/// Any change to bit positions or field widths breaks every archetype's vertex shader simultaneously and
/// requires a version bump (Layout v2.0) plus a per-archetype shader migration. Don't.
/// ──────────────────────────────────────────────────────────────────────────────────────────────────────
/// </summary>
public partial class AntRenderer : Node3D
{
	public const float WorldSizeM = 100f;                // 100 m × 100 m world
	public const float SimToWorld = WorldSizeM / TyphonBridge.WorldSize;   // sim units → m (currently 0.005 m/unit)

	// CapsuleMesh dimensions — workers are ~5 mm long. Subpixel at Foot+ bands; Phase 0 starts at 30 m visible width, ants barely visible.
	// We oversize a bit during Phase 0 development so the user can confirm the pipeline is alive without zooming all the way in.
	public const float AntHeight = 0.02f;                // 2 cm — debuggable. Will shrink to ~5 mm in Phase 1.
	public const float AntRadius = 0.008f;               // 8 mm radius

	private const int Stride = 12;                       // floats per ant in RenderWorkerBuffer (Transform2D + color)

	// State texture is laid out as a 2D RGBA32F grid — STATE_TEX_WIDTH columns × N rows, where N grows with capacity.
	// Why 2D and not 1×N: GPU 2D-texture dimensions are capped at 16384 on D3D12/Vulkan — a 200 k-wide 1D texture silently fails to create.
	private const int StateTexWidth = 2048;

	// VisualInstance3D render layer for the ant MultiMesh. The minimap camera excludes this layer (see Main.BuildMinimap)
	// so the tight viewport-AABB hint to TyphonBridge can drop off-camera ants from the RenderFrame without breaking the
	// minimap (which renders terrain + density + pheromone overlays, not individual capsules).
	public const uint AntsCullLayer = 1u << 1;  // layer 2

	private RenderBridge _bridge;

	// ── Render channels ────────────────────────────────────────────────────────
	// Two MultiMeshInstance3D nodes, each with its own mesh primitive + state texture:
	//   • _capsule : Worker / Larva / Queen — the original CapsuleMesh path.
	//   • _soldier : Soldier-only — PrismMesh (triangular wedge, "armoured" silhouette).
	// Routing happens in WriteBuffers based on the caste byte at data[off+1].
	// Both channels share the exact same vertex shader; the only thing that differs
	// is which Mesh subclass they bind to.
	private RenderChannel _capsule;
	private RenderChannel _soldier;

	private sealed class RenderChannel
	{
		public MultiMesh Mesh;
		public MultiMeshInstance3D Node;
		public ShaderMaterial Material;
		public ImageTexture StateTexture;
		public Image StateImage;
		public byte[] StateBytes;
		public int Capacity;
		public int Written;
	}

	// Phase 1 — density binning. 100×100 R8, one byte per 1 m world cell, saturating count (overflow stops at 255).
	private const int DensityRes = Terrain.DensityResolution;
	private byte[] _densityBytes = new byte[DensityRes * DensityRes];

	public byte[] DensityBytes => _densityBytes;
	public int DensityResolution => DensityRes;

	// Per-band render path switch. When false (Patch band) UpdateFromBridge:
	//   • still walks the per-worker buffers to author the density texture (cheap, ~0.5 ms at 200 k)
	//   • skips the state-texture pack + GPU upload (~3 MB/frame eliminated)
	//   • sets VisibleInstanceCount = 0 (no vertex-shader dispatch for individuals)
	private bool _drawIndividuals = true;

	// Last-frame perf counters surfaced to the HUD.
	public int LastUploadBytes { get; private set; }
	public bool LastDrewIndividuals => _drawIndividuals;

	public void SetBridge(RenderBridge bridge) => _bridge = bridge;
	public void SetHeightmapTexture(Texture2D heightmap)
	{
		_capsule?.Material?.SetShaderParameter("heightmap_tex", heightmap);
		_soldier?.Material?.SetShaderParameter("heightmap_tex", heightmap);
	}
	public void SetFadeIndividuals(float fade)
	{
		var clamped = Mathf.Clamp(fade, 0f, 1f);
		_capsule?.Material?.SetShaderParameter("fade_individuals", clamped);
		_soldier?.Material?.SetShaderParameter("fade_individuals", clamped);
	}
	public void SetMetresPerPixel(float mpp)
	{
		_capsule?.Material?.SetShaderParameter("metres_per_pixel", mpp);
		_soldier?.Material?.SetShaderParameter("metres_per_pixel", mpp);
	}
	public void SetBrightness(float brightness)
	{
		var clamped = Mathf.Clamp(brightness, 0f, 2f);
		_capsule?.Material?.SetShaderParameter("u_brightness", clamped);
		_soldier?.Material?.SetShaderParameter("u_brightness", clamped);
	}
	public void SetDrawIndividuals(bool draw) => _drawIndividuals = draw;

	public override void _Ready()
	{
		// Workers / Larva / Queens — original capsule path.
		var capsuleMesh = new CapsuleMesh
		{
			Height = AntHeight,
			Radius = AntRadius,
			RadialSegments = 6,
			Rings = 2,
		};
		_capsule = BuildChannel(capsuleMesh);

		// Soldiers — triangular prism, wider + shorter than the capsule. Reads as a
		// "shielded" silhouette at Loupe band so they're instantly distinguishable from
		// the capsule-shaped workers. Same state-texture / shader pipeline as the capsule
		// channel; only the bound Mesh subclass differs.
		var soldierMesh = new PrismMesh
		{
			Size = new Vector3(AntHeight * 1.4f, AntHeight * 0.9f, AntHeight * 1.0f),
			LeftToRight = 0.5f,
		};
		_soldier = BuildChannel(soldierMesh);
	}

	private RenderChannel BuildChannel(Mesh mesh)
	{
		var ch = new RenderChannel();
		ch.Mesh = new MultiMesh
		{
			TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
			UseColors = false,
			UseCustomData = false,
			Mesh = mesh,
			InstanceCount = 0,
		};

		var shader = new Shader { Code = ShaderSource };
		ch.Material = new ShaderMaterial { Shader = shader };
		ch.Material.SetShaderParameter("world_size", WorldSizeM);
		// Phase 5: scale_min..scale_max covers all 4 castes via a single byte:
		//   Larva ≈ 0.6×, Worker ≈ 1.0×, Soldier ≈ 1.3×, Queen ≈ 2.0× (clamped to scale_max).
		ch.Material.SetShaderParameter("scale_min", 0.6f);
		ch.Material.SetShaderParameter("scale_max", 2.0f);
		ch.Material.SetShaderParameter("ant_height_half", AntHeight * 0.5f);
		ch.Material.SetShaderParameter("fade_individuals", 1f);
		ch.Material.SetShaderParameter("metres_per_pixel", 0.03f);
		ch.Material.SetShaderParameter("min_pixels", 2.5f);
		ch.Material.SetShaderParameter("u_brightness", 1f);   // Phase 6A — Daisyworld × day/night multiplier
		mesh.SurfaceSetMaterial(0, ch.Material);

		ch.Node = new MultiMeshInstance3D
		{
			Multimesh = ch.Mesh,
			CustomAabb = new Aabb(new Vector3(-1, -5, -1), new Vector3(WorldSizeM + 2, 10, WorldSizeM + 2)),
			Layers = AntsCullLayer,
		};
		AddChild(ch.Node);
		return ch;
	}

	public void UpdateFromBridge()
	{
		if (_bridge == null) return;
		var frame = _bridge.GetLatest();
		if (frame?.Buffers == null) return;

		int total = 0;
		for (int i = 0; i < frame.Buffers.Length; i++) total += frame.Buffers[i].Count;

		if (total <= 0)
		{
			_capsule.Mesh.VisibleInstanceCount = 0;
			_soldier.Mesh.VisibleInstanceCount = 0;
			LastUploadBytes = 0;
			System.Array.Clear(_densityBytes, 0, _densityBytes.Length);
			return;
		}

		// Density binning runs every frame regardless of band — the terrain shader samples it at Patch zoom.
		// State-texture pack + upload + multimesh draw only run when we're actually drawing individuals (Loupe/Foot).
		if (_drawIndividuals)
		{
			// Pessimistic capacity: both channels sized to `total` so any caste mix fits without
			// a mid-write resize. Wasted texels at the tail get cleared in WriteBuffers, so
			// VisibleInstanceCount precisely bounds what the GPU actually draws.
			EnsureChannelCapacity(_capsule, total);
			EnsureChannelCapacity(_soldier, total);
			WriteBuffers(frame.Buffers, writeState: true);

			UploadChannel(_capsule);
			UploadChannel(_soldier);
			LastUploadBytes = (StateTexHeight(_capsule) + StateTexHeight(_soldier)) * StateTexWidth * 16;

			SetChannelVisible(_capsule);
			SetChannelVisible(_soldier);
		}
		else
		{
			// Patch band — skip the GPU upload entirely. Density-only pass through the buffers.
			WriteBuffers(frame.Buffers, writeState: false);
			_capsule.Mesh.VisibleInstanceCount = 0;
			_soldier.Mesh.VisibleInstanceCount = 0;
			LastUploadBytes = 0;
		}
	}

	private void UploadChannel(RenderChannel ch)
	{
		if (ch.StateImage == null) return;
		int h = StateTexHeight(ch);
		ch.StateImage.SetData(StateTexWidth, h, false, Image.Format.Rgbaf, ch.StateBytes);
		ch.StateTexture.Update(ch.StateImage);
	}

	private void SetChannelVisible(RenderChannel ch)
	{
		if (ch.Mesh.InstanceCount < ch.Written)
		{
			int grown = ch.Written + ch.Written / 4;
			ResizeMultiMesh(ch.Mesh, grown);
		}
		ch.Mesh.VisibleInstanceCount = ch.Written;
	}

	/// <summary>
	/// Walks the per-worker buffers in a single pass and fills the density texture; if <paramref name="writeState"/>
	/// is true, also packs the state-texture bytes for this frame. Skipping state-pack at Patch band drops ~16 B per
	/// ant of inner-loop work + the subsequent ~3 MB GPU upload.
	/// </summary>
	private unsafe void WriteBuffers(BufferSnapshot[] buffers, bool writeState)
	{
		const float simToU16  = 65535f / TyphonBridge.WorldSize;
		const float simToCell = DensityRes / TyphonBridge.WorldSize;

		System.Array.Clear(_densityBytes, 0, _densityBytes.Length);

		if (writeState)
		{
			fixed (byte* capDstBase = _capsule.StateBytes)
			fixed (byte* solDstBase = _soldier.StateBytes)
			fixed (byte* densBase = _densityBytes)
			{
				uint* capDst = (uint*)capDstBase;
				uint* solDst = (uint*)solDstBase;
				int capIdx = 0;
				int solIdx = 0;
				for (int b = 0; b < buffers.Length; b++)
				{
					var snap = buffers[b];
					if (snap.Data == null || snap.Count == 0) continue;
					var data = snap.Data;
					int count = snap.Count;
					for (int i = 0; i < count; i++)
					{
						int off = i * Stride;
						float simX = data[off + 3];
						float simZ = data[off + 7];
						float r    = data[off + 8];
						float g    = data[off + 9];
						float bl   = data[off + 10];

						uint px = (uint)Mathf.Clamp((int)(simX * simToU16), 0, 65535);
						uint pz = (uint)Mathf.Clamp((int)(simZ * simToU16), 0, 65535);
						uint texelR = px | (pz << 16);
						uint texelG = 0u;
						// Caste-driven scale byte. Mapping is hand-tuned to land each caste at the
						// right visual scale given scale_min=0.6, scale_max=2.0:
						//   Larva (2)   → 0    → 0.6×
						//   Worker (0)  → 73   → 1.0×
						//   Soldier (1) → 128  → 1.3×
						//   Queen (3)   → 255  → 2.0×
						int casteIdx = Mathf.Clamp((int)data[off + 1], 0, 3);
						uint scale = casteIdx switch
						{
							2 => 0u,
							0 => 73u,
							1 => 128u,
							3 => 255u,
							_ => 73u,
						};
						uint rb = (uint)Mathf.Clamp((int)(r  * 255f), 0, 255);
						uint gb = (uint)Mathf.Clamp((int)(g  * 255f), 0, 255);
						uint bb = (uint)Mathf.Clamp((int)(bl * 255f), 0, 255);
						uint texelB = scale | (rb << 8) | (gb << 16) | (bb << 24);

						// Route: soldiers go to the prism channel, everyone else to the capsule channel.
						bool isSoldier = casteIdx == 1;
						uint* outDst = isSoldier ? solDst : capDst;
						int outIdx = isSoldier ? solIdx : capIdx;
						int baseIdx = outIdx * 4;
						outDst[baseIdx + 0] = texelR;
						outDst[baseIdx + 1] = texelG;
						outDst[baseIdx + 2] = texelB;
						outDst[baseIdx + 3] = 0u;
						if (isSoldier) solIdx++; else capIdx++;

						int cellX = Mathf.Clamp((int)(simX * simToCell), 0, DensityRes - 1);
						int cellZ = Mathf.Clamp((int)(simZ * simToCell), 0, DensityRes - 1);
						byte* slot = densBase + (cellZ * DensityRes + cellX);
						if (*slot < 255) (*slot)++;
					}
				}
				// Zero unused tail in each channel so stale data doesn't leak when VisibleInstanceCount drops.
				ZeroTail(capDst, capIdx, _capsule.Capacity);
				ZeroTail(solDst, solIdx, _soldier.Capacity);
				_capsule.Written = capIdx;
				_soldier.Written = solIdx;
			}
		}
		else
		{
			// Density-only pass — Patch band. Skip state-texture pack entirely.
			fixed (byte* densBase = _densityBytes)
			{
				for (int b = 0; b < buffers.Length; b++)
				{
					var snap = buffers[b];
					if (snap.Data == null || snap.Count == 0) continue;
					var data = snap.Data;
					int count = snap.Count;
					for (int i = 0; i < count; i++)
					{
						int off = i * Stride;
						float simX = data[off + 3];
						float simZ = data[off + 7];
						int cellX = Mathf.Clamp((int)(simX * simToCell), 0, DensityRes - 1);
						int cellZ = Mathf.Clamp((int)(simZ * simToCell), 0, DensityRes - 1);
						byte* slot = densBase + (cellZ * DensityRes + cellX);
						if (*slot < 255) (*slot)++;
					}
				}
			}
		}
	}

	private static unsafe void ZeroTail(uint* dst, int writeIdx, int capacity)
	{
		for (int i = writeIdx; i < capacity; i++)
		{
			int baseIdx = i * 4;
			dst[baseIdx + 0] = 0;
			dst[baseIdx + 1] = 0;
			dst[baseIdx + 2] = 0;
			dst[baseIdx + 3] = 0;
		}
	}

	private static int StateTexHeight(RenderChannel ch) => (ch.Capacity + StateTexWidth - 1) / StateTexWidth;

	private void EnsureChannelCapacity(RenderChannel ch, int requested)
	{
		if (ch.StateImage != null && ch.Capacity >= requested) return;

		// Grow with 25% headroom to amortise re-allocations; round up to a multiple of StateTexWidth so the texture is rectangular.
		int newCap = ch.Capacity == 0 ? Math.Max(requested, 4096) : Math.Max(requested, ch.Capacity * 2);
		newCap = ((newCap + StateTexWidth - 1) / StateTexWidth) * StateTexWidth;
		ch.Capacity = newCap;
		ch.StateBytes = new byte[newCap * 16];

		int texHeight = StateTexHeight(ch);
		ch.StateImage = Image.CreateFromData(StateTexWidth, texHeight, false, Image.Format.Rgbaf, ch.StateBytes);
		if (ch.StateTexture == null)
		{
			ch.StateTexture = ImageTexture.CreateFromImage(ch.StateImage);
			ch.Material.SetShaderParameter("state_tex", ch.StateTexture);
			ch.Material.SetShaderParameter("state_tex_width", StateTexWidth);
		}
		else
		{
			ch.StateTexture.SetImage(ch.StateImage);
			ch.Material.SetShaderParameter("state_tex", ch.StateTexture);
		}
	}

	private static void ResizeMultiMesh(MultiMesh mm, int newInstanceCount)
	{
		// Grow MultiMesh.InstanceCount in place. Each instance's transform stays at identity — set them once on grow.
		int oldCount = mm.InstanceCount;
		mm.InstanceCount = newInstanceCount;
		for (int i = oldCount; i < newInstanceCount; i++)
		{
			mm.SetInstanceTransform(i, Transform3D.Identity);
		}
	}

	// ── Shader source ──────────────────────────────────────────────────────────
	//
	// Reads texel.r/g/b/a as floats then reinterprets via floatBitsToUint. Avoids the usampler2D + RenderingDevice plumbing —
	// adequate for Phase 0; will switch to UINT format in Phase 2 if profiling shows the reinterpret has cost.

	private const string ShaderSource = @"
shader_type spatial;
// blend_mix + depth_prepass_alpha so the LOD fade alpha actually disappears the ants smoothly at the Foot→Patch transition.
render_mode unshaded, cull_disabled, blend_mix, depth_prepass_alpha;

uniform sampler2D state_tex     : filter_nearest, repeat_disable;
uniform sampler2D heightmap_tex : filter_linear,  repeat_disable;
uniform int       state_tex_width;
uniform float     world_size;
uniform float     scale_min;
uniform float     scale_max;
uniform float     ant_height_half;
uniform float     fade_individuals;
// Sub-pixel anti-flicker: when an ant would be smaller than min_pixels in screen-space, scale it up to stay rasterizable.
// metres_per_pixel — approximate at the camera's target depth (works for both ortho and perspective).
uniform float     metres_per_pixel;
uniform float     min_pixels;
uniform float     u_brightness;  // Phase 6A — Daisyworld × day/night multiplier on final ALBEDO

varying vec3 v_color;

void vertex() {
    // State texture is a 2D grid of state_tex_width columns; INSTANCE_ID indexes into it row-major.
    int idx = int(INSTANCE_ID);
    ivec2 coord = ivec2(idx % state_tex_width, idx / state_tex_width);
    uvec4 packed = floatBitsToUint(texelFetch(state_tex, coord, 0));

    vec2 pos_xz = vec2(float(packed.r & uint(0xFFFF)),
                       float((packed.r >> uint(16)) & uint(0xFFFF))) * (world_size / 65535.0);
    float yaw   = float(packed.g & uint(0xFFFF)) * (6.2831853 / 65536.0);
    float scale = mix(scale_min, scale_max, float(packed.b & uint(0xFF)) / 255.0);

    // Sub-pixel guard. The capsule's nominal world-height = ant_height_half * 2 * scale. We want it to render at least
    // min_pixels in screen space. If the capsule is smaller than that, scale it up so it stays visible. Breaks bug-scale
    // realism at zoom-out but the eye can't resolve 0.5 px detail anyway — it just stops flickering.
    float required_size = metres_per_pixel * min_pixels;
    float current_size  = ant_height_half * 2.0 * scale;
    if (current_size < required_size) {
        scale *= required_size / current_size;
    }

    float cs = cos(yaw);
    float sn = sin(yaw);
    vec3 local = VERTEX * scale;
    vec3 rotated = vec3(local.x * cs - local.z * sn,
                        local.y,
                        local.x * sn + local.z * cs);

    float ground_y = texture(heightmap_tex, pos_xz / world_size).r;
    vec3 world_pos = rotated + vec3(pos_xz.x, ground_y + ant_height_half, pos_xz.y);
    POSITION = PROJECTION_MATRIX * (VIEW_MATRIX * vec4(world_pos, 1.0));

    float cr = float((packed.b >> uint( 8)) & uint(0xFF)) / 255.0;
    float cg = float((packed.b >> uint(16)) & uint(0xFF)) / 255.0;
    float cb = float((packed.b >> uint(24)) & uint(0xFF)) / 255.0;
    v_color  = vec3(cr, cg, cb);
}

void fragment() {
    ALBEDO = v_color * u_brightness;
    ALPHA  = fade_individuals;
}
";
}
