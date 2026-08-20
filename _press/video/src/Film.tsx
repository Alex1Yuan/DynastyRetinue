import {
  AbsoluteFill,
  Sequence,
  staticFile,
  Series,
  useCurrentFrame,
  useVideoConfig,
} from "remotion";
import { Audio, Video } from "@remotion/media";
import { TitleCard } from "./TitleCard";
import { FeatureBars, GROWTH_ITEMS } from "./FeatureBars";
import { ShipStats } from "./ShipStats";
import { UninstallCard, EndCard } from "./Cards";
import { Subtitle, HardSub } from "./Subtitle";

/**
 * 完整宣传片。
 *
 * ★素材为什么是切好的小片段而不是三段原录屏★
 *   原片是 4K，三段共 6.1 GB。让 Remotion 在里面反复 seek 慢得离谱，
 *   而且 public/ 里塞 6 GB 也不合适。切片在 tools 里用 ffmpeg 做好了。
 *
 * ★UI 段（招募面板 / 船坞对话框）是裁切过的，不是整帧缩放★
 *   4K 整帧缩到 1080p 是 0.5×，面板里那些说明文字只剩十几像素，
 *   B站二压之后必糊。裁到面板本身是 0.75×，字大一倍，
 *   顺带把右上角的 FPS 叠层和空荡荡的画面边缘切掉了。
 *   战斗段没有小字，整帧缩放看不出差别，所以照常用全画幅。
 *
 * ★时间轴按配音实测长度排，每段多留 2–4 秒★
 *   配音说完立刻切下一段会很赶，留白也给转场腾地方。
 */

const F = (s: number) => Math.round(s * 60);

/**
 * 一段素材。
 *
 * ★`zoom` 为什么存在★
 *   录像 B 是同一场战斗、同一个房间，而且"镜头不跟随卫兵"正是这个 mod 的卖点 ——
 *   所以六分钟里画面几乎是静止的，隔两分钟切出来的两块看着一模一样。
 *   给一个极缓的推进（每秒 0.45%），观感上就有了运动，也不需要伪造任何东西。
 *   ★UI 段一律不加★：缩放会让面板文字每帧重采样，越推越糊，正好抵消掉裁切的意义。
 */
const Clip: React.FC<{
  src: string;
  from?: number;
  /** 每秒放大的比例；0 = 完全静止 */
  zoom?: number;
  /** 推进落点，CSS transform-origin 语法。换落点是相邻镜头之间最省事的区分手段 */
  origin?: string;
}> = ({ src, from = 0, zoom = 0, origin = "center" }) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  return (
    <Video
      src={staticFile(`footage/${src}`)}
      trimBefore={F(from)}
      style={{
        width: "100%",
        height: "100%",
        objectFit: "cover",
        transformOrigin: origin,
        scale: zoom ? 1 + (frame / fps) * zoom : undefined,
      }}
    />
  );
};

/** 段落容器：画面 + 配音 + 该段字幕，三者共用同一个本地时钟。 */
const Seg: React.FC<{
  vo: string;
  track: string;
  durationInFrames: number;
  children: React.ReactNode;
}> = ({ vo, track, durationInFrames, children }) => (
  <AbsoluteFill name={`Seg_${track}`}>
    {children}
    <Audio src={staticFile(`vo/${vo}`)} />
    <Sequence durationInFrames={durationInFrames} layout="none">
      <Subtitle track={track} />
    </Sequence>
  </AbsoluteFill>
);

export const Film: React.FC = () => {
  useVideoConfig();

  return (
    <AbsoluteFill name="Film" style={{ backgroundColor: "#000" }}>
      <Series>
        {/* ── 冷开场：先给结果，再解释这是什么 ───────────────── */}
        <Series.Sequence durationInFrames={F(13)} name="冷开场">
          <Clip src="b_combat1.mp4" zoom={0.0045} origin="52% 46%" />
          <HardSub text="这五个人不是我在操作" fadeIn={1.2} hold={7} />
        </Series.Sequence>

        <Series.Sequence durationInFrames={F(4)} name="标题">
          <TitleCard />
        </Series.Sequence>

        {/* ── 01 这是什么（配音 20.9s） ───────────────────────── */}
        <Series.Sequence durationInFrames={F(24)} name="这是什么">
          <Seg vo="01_这是什么.mp3" track="01" durationInFrames={F(24)}>
            <Series>
              <Series.Sequence durationInFrames={F(9)}>
                <Clip src="a_recruit.mp4" />
              </Series.Sequence>
              <Series.Sequence durationInFrames={F(15)}>
                <Clip src="b_combat1.mp4" from={15} zoom={0.005} origin="40% 55%" />
              </Series.Sequence>
            </Series>
          </Seg>
        </Series.Sequence>

        {/* ── 02 镜头（配音 23.3s） ───────────────────────────── */}
        <Series.Sequence durationInFrames={F(26)} name="镜头">
          <Seg vo="02_镜头.mp3" track="02" durationInFrames={F(26)}>
            <Clip src="b_combat2.mp4" zoom={0.004} origin="60% 50%" />
          </Seg>
        </Series.Sequence>

        {/* ── 03 招募与成长（配音 33.2s） ─────────────────────── */}
        {/* 招募面板在原片里只开了 25–35 秒这十秒，给 9 秒已经是全部了； */}
        {/* 后面靠实战画面说明"它们自己会长"，比对着一个静止面板念更有说服力 */}
        <Series.Sequence durationInFrames={F(36)} name="招募与成长">
          <Seg vo="03_招募与成长.mp3" track="03" durationInFrames={F(36)}>
            <Series>
              <Series.Sequence durationInFrames={F(9)}>
                <Clip src="a_recruit.mp4" />
              </Series.Sequence>
              <Series.Sequence durationInFrames={F(13)}>
                <Clip src="b_combat4.mp4" zoom={0.006} origin="55% 60%" />
              </Series.Sequence>
              <Series.Sequence durationInFrames={F(14)}>
                <Clip src="b_combat5.mp4" zoom={0.0035} origin="46% 48%" />
                {/* 只放前两条：另外两条是「精英」和「座舰」两段的内容，
                    提前打出来跟正在念的旁白对不上，也把后面的揭示提前用掉了 */}
                <Sequence from={F(1)} layout="none">
                  <FeatureBars items={GROWTH_ITEMS} />
                </Sequence>
              </Series.Sequence>
            </Series>
          </Seg>
        </Series.Sequence>

        {/* ── 04 精英与代价（配音 34.3s） ─────────────────────── */}
        <Series.Sequence durationInFrames={F(37)} name="精英与代价">
          <Seg vo="04_精英与代价.mp3" track="04" durationInFrames={F(37)}>
            <Series>
              <Series.Sequence durationInFrames={F(21)}>
                <Clip src="b_combat3.mp4" zoom={0.0045} origin="38% 58%" />
              </Series.Sequence>
              <Series.Sequence durationInFrames={F(16)}>
                <Clip src="b_combat2.mp4" from={14} zoom={0.005} origin="45% 42%" />
              </Series.Sequence>
            </Series>
          </Seg>
        </Series.Sequence>

        {/* ── 05 座舰改装（配音 26.2s） ───────────────────────── */}
        <Series.Sequence durationInFrames={F(30)} name="座舰改装">
          <Seg vo="05_座舰改装.mp3" track="05" durationInFrames={F(30)}>
            <Series>
              <Series.Sequence durationInFrames={F(7)}>
                <Clip src="a_refit.mp4" />
              </Series.Sequence>
              {/* 装备界面这一段 tooltip 上有「Mod 已计算」，是改装真生效最直接的一帧 */}
              <Series.Sequence durationInFrames={F(8)}>
                <Clip src="a_shipeq.mp4" />
              </Series.Sequence>
              <Series.Sequence durationInFrames={F(15)}>
                <Clip src="c_void1.mp4" zoom={0.003} origin="center" />
              </Series.Sequence>
            </Series>
            {/* 数值对照压在虚空战开头：配音念到"护盾和装甲翻倍"时正好滚到那两行。
                ★必须走 overlay 模式★ —— ShipStats 默认那个 INK 底是实心的，
                直接叠上去等于切一整帧黑屏，虚空战完全看不见 */}
            <Sequence from={F(15)} durationInFrames={F(8)} layout="none">
              <ShipStats overlay />
            </Sequence>
          </Seg>
        </Series.Sequence>

        {/* ── 06 卸载安全（配音 33.2s） ───────────────────────── */}
        <Series.Sequence durationInFrames={F(36)} name="卸载安全">
          <Seg vo="06_卸载安全.mp3" track="06" durationInFrames={F(36)}>
            <UninstallCard />
          </Seg>
        </Series.Sequence>

        <Series.Sequence durationInFrames={F(6)} name="尾板">
          <EndCard />
        </Series.Sequence>
      </Series>
    </AbsoluteFill>
  );
};
