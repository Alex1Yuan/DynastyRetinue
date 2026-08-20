import {
  AbsoluteFill,
  Easing,
  Interactive,
  interpolate,
  useCurrentFrame,
  useVideoConfig,
} from "remotion";
import { FONT, GOLD, INK, MUTED, TEXT } from "./theme";

/**
 * 片头卡 —— 3 秒。
 *
 * 刻意做得很静：整个片子的开场是「五个人在自己打」那段实机画面，
 * 这张卡是接在它后面的第二拍，不该再抢注意力。
 * 所以只有一条金线扫开 + 文字淡入，没有任何飞入或缩放。
 */
export const TitleCard: React.FC = () => {
  const frame = useCurrentFrame();
  const { fps, width } = useVideoConfig();

  return (
    <AbsoluteFill
      name="TitleCard"
      style={{
        backgroundColor: INK,
        fontFamily: FONT,
        display: "flex",
        flexDirection: "column",
        justifyContent: "center",
        alignItems: "center",
      }}
    >
      {/* 顶部与底部的暗角，让实机画面切进来时不会突兀 */}
      <AbsoluteFill
        name="Vignette"
        style={{
          background:
            "radial-gradient(ellipse at center, rgba(200,164,92,0.07) 0%, rgba(0,0,0,0) 60%)",
        }}
      />

      <Interactive.Div
        name="RuleTop"
        style={{
          height: 2,
          backgroundColor: GOLD,
          marginBottom: 56,
          width: interpolate(frame, [0, 0.9 * fps], [0, width * 0.42], {
            extrapolateLeft: "clamp",
            extrapolateRight: "clamp",
            easing: Easing.bezier(0.16, 1, 0.3, 1),
          }),
          opacity: 0.9,
        }}
      />

      <Interactive.Div
        name="TitleZh"
        style={{
          fontSize: 128,
          fontWeight: 700,
          color: TEXT,
          letterSpacing: 4,
          opacity: interpolate(frame, [0.35 * fps, 1.1 * fps], [0, 1], {
            extrapolateLeft: "clamp",
            extrapolateRight: "clamp",
            easing: Easing.bezier(0.16, 1, 0.3, 1),
          }),
        }}
      >
        家族卫队 · 座舰改装
      </Interactive.Div>

      <Interactive.Div
        name="TitleEn"
        style={{
          fontSize: 44,
          color: GOLD,
          letterSpacing: 14,
          marginTop: 26,
          opacity: interpolate(frame, [0.7 * fps, 1.5 * fps], [0, 1], {
            extrapolateLeft: "clamp",
            extrapolateRight: "clamp",
            easing: Easing.bezier(0.16, 1, 0.3, 1),
          }),
        }}
      >
        DYNASTY RETINUE &amp; REFIT
      </Interactive.Div>

      <Interactive.Div
        name="RuleBottom"
        style={{
          height: 2,
          backgroundColor: GOLD,
          marginTop: 56,
          width: interpolate(frame, [0.2 * fps, 1.1 * fps], [0, width * 0.42], {
            extrapolateLeft: "clamp",
            extrapolateRight: "clamp",
            easing: Easing.bezier(0.16, 1, 0.3, 1),
          }),
          opacity: 0.9,
        }}
      />

      <Interactive.Div
        name="Subtitle"
        style={{
          fontSize: 32,
          color: MUTED,
          letterSpacing: 3,
          marginTop: 44,
          opacity: interpolate(frame, [1.2 * fps, 2 * fps], [0, 1], {
            extrapolateLeft: "clamp",
            extrapolateRight: "clamp",
            easing: Easing.bezier(0.16, 1, 0.3, 1),
          }),
        }}
      >
        战锤40K：行商浪人　·　MOD
      </Interactive.Div>
    </AbsoluteFill>
  );
};
