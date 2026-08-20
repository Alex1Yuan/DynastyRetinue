import { Interactive, interpolate, useCurrentFrame, useVideoConfig } from "remotion";
import { CAPTIONS, Cue } from "./captions";
import { FONT } from "./theme";

/**
 * 字幕条。
 *
 * ★为什么自己写而不是用 @remotion/captions★
 *   那个包按词做高亮，需要词级时间戳；edge-tts 给的是整句区间。
 *   硬套只会让整句一起闪，不如直接按句渲染。
 *
 * ★为什么按 start 取"最后一条命中的"而不是 filter★
 *   edge-tts 的区间会重叠（第 1 条 end=6.069，第 2 条 start=6.019）。
 *   filter 会在那 50 毫秒里同时渲染两条，画面上是一次抖动。
 *   取最后一条 = 后来的覆盖先前的，视觉上就是正常的切换。
 */
export const Subtitle: React.FC<{ track: keyof typeof CAPTIONS | string }> = ({ track }) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const t = frame / fps;

  const cues = (CAPTIONS as Record<string, Cue[]>)[track] ?? [];
  let cur: Cue | null = null;
  for (const c of cues) {
    if (t >= c.from && t < c.to) cur = c;
  }
  if (!cur) return null;

  return (
    <Interactive.Div
      name="Subtitle"
      style={{
        position: "absolute",
        left: 0,
        right: 0,
        bottom: 88,
        display: "flex",
        justifyContent: "center",
        pointerEvents: "none",
        opacity: interpolate(frame, [cur.from * fps, cur.from * fps + 0.12 * fps], [0, 1], {
          extrapolateLeft: "clamp",
          extrapolateRight: "clamp",
        }),
      }}
    >
      <Interactive.Div
        name="SubtitleText"
        style={{
          fontFamily: FONT,
          fontSize: 42,
          lineHeight: 1.45,
          color: "#f2eee4",
          maxWidth: 1480,
          textAlign: "center",
          padding: "12px 30px",
          borderRadius: 6,
          background: "rgba(6,7,9,0.62)",
          // 半透明底 + 描边双保险：底色救亮画面，描边救底色也压不住的高光
          textShadow: "0 2px 6px rgba(0,0,0,0.95), 0 0 2px rgba(0,0,0,0.9)",
        }}
      >
        {cur.text}
      </Interactive.Div>
    </Interactive.Div>
  );
};

/** 冷开场那句压屏字，没有配音，所以不走 CAPTIONS。 */
export const HardSub: React.FC<{ text: string; fadeIn?: number; hold?: number }> = ({
  text,
  fadeIn = 0.8,
  hold = 6,
}) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  return (
    <Interactive.Div
      name="HardSub"
      style={{
        position: "absolute",
        left: 0,
        right: 0,
        bottom: 150,
        display: "flex",
        justifyContent: "center",
        opacity: interpolate(
          frame,
          [0, fadeIn * fps, (fadeIn + hold) * fps, (fadeIn + hold + 0.7) * fps],
          [0, 1, 1, 0],
          { extrapolateLeft: "clamp", extrapolateRight: "clamp" },
        ),
      }}
    >
      <Interactive.Div
        name="HardSubText"
        style={{
          fontFamily: FONT,
          fontSize: 58,
          letterSpacing: 6,
          color: "#f2eee4",
          textShadow: "0 3px 14px rgba(0,0,0,0.95)",
        }}
      >
        {text}
      </Interactive.Div>
    </Interactive.Div>
  );
};
