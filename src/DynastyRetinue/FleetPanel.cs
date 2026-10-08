using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace DynastyRetinue.UI
{
    /// <summary>
    /// 卫队管理窗口中的护航舰队面板；由外层持有 Canvas、导航和预算状态条。
    /// 只响应建窗、切页及编辑事件，名册与预算规则统一交给现有服务。
    /// </summary>
    internal static class FleetPanel
    {
        private static GameObject _root;
        private static Transform _content;
        private static ScrollRect _scroll;
        private static TextMeshProUGUI _reply;
        private static Button _tabRoster, _tabRecruit;
        private static Action _onBudgetChanged;
        private static int _page;
        private static int _recruitClass;
        private static string _recruitHull = "";
        private static readonly Dictionary<string, string> FleetNameDrafts
            = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly HashSet<string> ExpandedEquipmentCards
            = new HashSet<string>(StringComparer.Ordinal);
        private static string _expandedLoadout = "";
        private static string _replyText = "";

        /// <summary>parent 已由外层定位；本面板只使用其内部坐标。</summary>
        internal static void Build(Transform parent, Action onBudgetChanged)
        {
            Reset();
            if (parent == null) return;
            try
            {
                _root = RetinueUI.NewUI("FleetPanel", parent);
                RetinueUI.StretchPadPublic(_root, 0f, 0f);
                _onBudgetChanged = onBudgetChanged;

                _tabRoster = RetinueUI.MakeButton(_root.transform, L.T("在册"),
                    200f, 40f, delegate { SwitchPage(0); });
                PlaceTopLeft(_tabRoster.gameObject, 0f, -4f);
                _tabRecruit = RetinueUI.MakeButton(_root.transform, L.T("招募"),
                    200f, 40f, delegate { SwitchPage(1); });
                PlaceTopLeft(_tabRecruit.gameObject, 208f, -4f);

                _content = RetinueUI.MakeScrollArea(_root.transform, 52f);
                _scroll = _content.parent.parent.GetComponent<ScrollRect>();
                var scrollRT = (RectTransform)_scroll.transform;
                scrollRT.offsetMin = new Vector2(scrollRT.offsetMin.x, 72f);
                AddScrollbar(_scroll);

                _reply = RetinueUI.MakeLabelPublic(_root.transform, "", 18f,
                    VanillaSkin.Text, TextAlignmentOptions.TopLeft);
                var replyRT = (RectTransform)_reply.transform;
                replyRT.anchorMin = Vector2.zero;
                replyRT.anchorMax = new Vector2(1f, 0f);
                replyRT.pivot = new Vector2(0.5f, 0f);
                replyRT.offsetMin = new Vector2(14f, 8f);
                replyRT.offsetMax = new Vector2(-14f, 64f);
                _reply.overflowMode = TextOverflowModes.Overflow;

                Refresh();
            }
            catch (Exception e)
            {
                Main.LogError("[舰队UI] 建面板失败: " + e);
                Reset();
            }
        }

        /// <summary>外层关闭/重建时调用；不保留旧窗口、输入草稿或外层回调。</summary>
        internal static void Reset()
        {
            var root = _root;
            _onBudgetChanged = null;
            _root = null;
            _content = null;
            _scroll = null;
            _reply = null;
            _tabRoster = null;
            _tabRecruit = null;
            _page = 0;
            _recruitClass = 0;
            _recruitHull = "";
            _expandedLoadout = "";
            _replyText = "";
            try
            {
                if (root != null)
                {
                    root.SetActive(false);
                    UnityEngine.Object.Destroy(root);
                }
            }
            catch (Exception e) { Main.LogError("[舰队UI] 清理面板失败: " + e.Message); }
            FleetNameDrafts.Clear();
            ExpandedEquipmentCards.Clear();
            FleetHullPreview.Clear();
        }

        /// <summary>主线程事件刷新；回调仅刷新外层 PF 状态，不应再次刷新本面板。</summary>
        internal static void Refresh()
        {
            if (_root == null || _content == null) return;
            try
            {
                PaintTab(_tabRoster, _page == 0);
                PaintTab(_tabRecruit, _page == 1);
                if (_reply != null)
                    _reply.text = string.IsNullOrEmpty(_replyText)
                        ? L.T("舰队变更随游戏存档保存；读取旧档会恢复当时的舰队。")
                        : L.F("操作回执：{0}", _replyText);

                // Destroy 延迟至帧尾；先停用旧行，避免布局和点击仍命中另一页的内容。
                for (int i = _content.childCount - 1; i >= 0; i--)
                {
                    var child = _content.GetChild(i).gameObject;
                    child.SetActive(false);
                    UnityEngine.Object.Destroy(child);
                }
                if (_page == 0) RebuildRosterRows();
                else MakeRecruitRow();

                RetinueUI.ReapplyLayer();
                // 复用外层事件触发的有限次补层，覆盖稍后生成的 TMP fallback 子网格。
                RetinueUI.MarkLayerDirty();
                if (_onBudgetChanged != null) _onBudgetChanged();
            }
            catch (Exception e) { Main.LogError("[舰队UI] 刷新失败: " + e); }
        }

        private static void SwitchPage(int page)
        {
            int next = page == 1 ? 1 : 0;
            if (_root == null || _page == next) return;
            _page = next;
            _expandedLoadout = "";
            _replyText = "";
            if (_scroll != null) _scroll.StopMovement();
            Refresh();
            if (_content != null) ((RectTransform)_content).anchoredPosition = Vector2.zero;
        }

        private static void PaintTab(Button button, bool active)
        {
            if (button == null) return;
            var image = button.GetComponentInChildren<Image>();
            if (image != null) image.color = active ? VanillaSkin.Gold : Color.white;
        }

        internal static string BudgetText()
        {
            var roster = SpaceEscortService.CurrentRoster(true);
            var entries = roster != null && roster.Entries != null
                ? roster.Entries : new List<SpaceFleetEntry>();
            int capacity = 0;
            foreach (var entry in entries)
            {
                var hull = entry != null ? SpaceFleetCatalog.Find(entry.BlueprintGuid) : null;
                if (hull != null) capacity += hull.Capacity;
            }
            long used = FleetBudget.Used(entries);
            int limit = FleetBudget.Limit();
            string state = limit < 0
                ? L.T("预算不可读取；减配、同价替换仍可")
                : used > limit
                    ? L.F("超预算 {0}；减配、同价替换仍可", used - limit)
                    : L.F("剩余 {0}", limit - used);
            return L.F("PF 已占/上限 <color=#c6a24e>{0}/{1}</color>　容量 <color=#c6a24e>{2}/{3}</color>　{4}　<size=15>只占用，不扣除利润因子</size>",
                used, limit >= 0 ? limit.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?",
                capacity, SpaceEscortService.FleetCapacity, state);
        }

        private static void RebuildRosterRows()
        {
            var roster = SpaceEscortService.CurrentRoster(true);
            var entries = roster != null && roster.Entries != null
                ? new List<SpaceFleetEntry>(roster.Entries) : new List<SpaceFleetEntry>();
            entries.Sort((a, b) => string.CompareOrdinal(a != null ? a.Id : "",
                b != null ? b.Id : ""));
            if (entries.Count == 0)
            {
                MakeInfoRow(L.T("舰队名册为空，本场不会部署僚舰。"));
                return;
            }
            foreach (var entry in entries)
                if (entry != null) MakeFleetCard(entry, roster != null ? roster.GameId : "");
        }

        private static void MakeRecruitRow()
        {
            MakeInfoRow(L.T("先选择舰级，再选择具体舰型；招募使用该舰型的原装炮位、装备和 AI。"));
            GameObject classes = MakeCard("FleetClasses", 64f);
            for (int shipClass = 0; shipClass < 3; shipClass++)
            {
                int selectedClass = shipClass;
                var button = RetinueUI.MakeButton(classes.transform, ClassName(shipClass),
                    330f, 40f, delegate
                    {
                        _recruitClass = selectedClass;
                        _replyText = "";
                        Refresh();
                        if (_content != null) ((RectTransform)_content).anchoredPosition = Vector2.zero;
                    });
                PlaceTopLeft(button.gameObject, 16f + shipClass * 346f, -12f);
                PaintTab(button, _recruitClass == shipClass);
            }

            var hulls = SpaceFleetCatalog.All();
            var choices = new List<SpaceFleetHullDef>();
            SpaceFleetHullDef selected = null;
            foreach (var hull in hulls)
            {
                if (hull == null || hull.Class != _recruitClass) continue;
                choices.Add(hull);
                if (string.Equals(hull.BlueprintGuid, _recruitHull, StringComparison.OrdinalIgnoreCase))
                    selected = hull;
            }
            if (choices.Count == 0)
            {
                MakeInfoRow(L.T("此舰级暂无可招募舰型。"));
                return;
            }
            if (selected == null) selected = choices[0];
            _recruitHull = selected.BlueprintGuid;

            GameObject row = MakeCard("FleetHullChoices", 48f + ((choices.Count + 1) / 2) * 48f);
            var heading = RetinueUI.MakeLabelPublic(row.transform,
                L.F("{0}：选择舰型", ClassName(_recruitClass)), 19f,
                VanillaSkin.Text, TextAlignmentOptions.Left);
            Place(heading.gameObject, 16f, -38f, 16f, -6f);
            ShowRecruitText(heading);
            for (int i = 0; i < choices.Count; i++)
            {
                var hull = choices[i];
                string localGuid = hull.BlueprintGuid;
                bool active = ReferenceEquals(hull, selected);
                Button button = RetinueUI.MakeButton(row.transform,
                    (active ? L.T("已选：") : L.T("可选："))
                    + L.F("{0}　舰体 {1} PF / 容量 {2}", SpaceFleetCatalog.DisplayName(hull),
                        FleetBudget.HullCost(hull.Class), hull.Capacity), 566f, 40f,
                    delegate
                    {
                        _recruitHull = localGuid;
                        _replyText = "";
                        Refresh();
                    });
                PlaceTopLeft(button.gameObject, 16f + (i % 2) * 582f, -42f - (i / 2) * 48f);
                PaintTab(button, active);
            }
            MakeHullDetails(selected);
        }

        private static string ClassName(int shipClass)
        {
            if (shipClass == 1) return L.T("巡洋舰");
            if (shipClass == 2) return L.T("大巡洋舰");
            return L.T("护卫舰");
        }

        private static void MakeHullDetails(SpaceFleetHullDef hull)
        {
            // Only the selected hull is resolved, on explicit UI refreshes; no Update polling.
            string reason;
            bool available = SpaceEscortService.HullAvailable(hull.BlueprintGuid, out reason);
            var model = ShipModelCatalog.ByPrefab(hull.PrefabAssetId);
            string notes = SpaceFleetCatalog.NativeAbilityNotes(hull);
            GameObject card = MakeCard("FleetHullDetails", 450f);
            var title = RetinueUI.MakeLabelPublic(card.transform,
                L.F("{0}　舰体 {1} PF / 容量 {2}", SpaceFleetCatalog.DisplayName(hull),
                    FleetBudget.HullCost(hull.Class), hull.Capacity), 20f,
                VanillaSkin.Text, TextAlignmentOptions.Left);
            Place(title.gameObject, 16f, -40f, 316f, -8f);
            ShowRecruitText(title);
            var modelText = RetinueUI.MakeLabelPublic(card.transform,
                L.F("船模：{0}", model != null ? model.HullName : SpaceFleetCatalog.DisplayName(hull)),
                17f, VanillaSkin.TextDim, TextAlignmentOptions.Left);
            Place(modelText.gameObject, 16f, -74f, 16f, -42f);
            ShowRecruitText(modelText);

            var previewBox = RetinueUI.NewUI("HullPreviewFrame", card.transform);
            RetinueUI.PaintPanel(previewBox.AddComponent<Image>(), null,
                new Color(0.035f, 0.045f, 0.045f, 1f));
            ((RectTransform)previewBox.transform).sizeDelta = new Vector2(500f, 330f);
            PlaceTopLeft(previewBox, 16f, -86f);
            Texture preview = FleetHullPreview.Get(hull);
            if (preview != null)
            {
                var art = RetinueUI.NewUI("HullModelPreview", previewBox.transform);
                var image = art.AddComponent<RawImage>();
                image.texture = preview;
                image.raycastTarget = false;
                // The generated image has this exact aspect ratio; no crop or portrait mask.
                ((RectTransform)art.transform).sizeDelta = new Vector2(500f, 300f);
                PlaceTopLeft(art, 0f, -15f);
            }
            else
            {
                var missing = RetinueUI.MakeLabelPublic(previewBox.transform,
                    L.T("模型预览暂不可用"), 18f, VanillaSkin.TextDim, TextAlignmentOptions.Center);
                RetinueUI.StretchPadPublic(missing.gameObject, 16f, 16f);
            }

            var stock = RetinueUI.MakeLabelPublic(card.transform, L.T("原装炮位与组件"),
                18f, VanillaSkin.Text, TextAlignmentOptions.Left);
            Place(stock.gameObject, 540f, -118f, 16f, -86f);
            ShowRecruitText(stock);
            float y = -124f;
            // Weapon rows first so the firing layout can be compared immediately.
            for (int pass = 0; pass < 2; pass++)
                foreach (var slot in hull.Slots)
                {
                    if (slot == null || slot.IsWeapon != (pass == 0)) continue;
                    var text = RetinueUI.MakeLabelPublic(card.transform,
                        L.F("{0}：{1}", SpaceEscortService.SlotDisplayName(slot),
                            SpaceEscortService.ItemDisplayName(slot.OriginalItemGuid,
                                !slot.IsWeapon ? SpaceEscortService.SlotDisplayName(slot) : null)),
                        17f, VanillaSkin.TextDim, TextAlignmentOptions.TopLeft);
                    text.textWrappingMode = TextWrappingModes.Normal;
                    text.overflowMode = TextOverflowModes.Overflow;
                    // Measure against a conservative column width before the parent layout runs.
                    float rowHeight = Mathf.Max(32f, text.GetPreferredValues(text.text, 600f, 0f).y + 6f);
                    Place(text.gameObject, 540f, y - rowHeight, 20f, y);
                    y -= rowHeight;
                }
            if (!string.IsNullOrEmpty(notes))
            {
                var nativeNotes = RetinueUI.MakeLabelPublic(card.transform, notes,
                    16f, VanillaSkin.TextDim, TextAlignmentOptions.TopLeft);
                nativeNotes.textWrappingMode = TextWrappingModes.Normal;
                nativeNotes.overflowMode = TextOverflowModes.Overflow;
                float notesHeight = Mathf.Max(42f, nativeNotes.GetPreferredValues(notes, 600f, 0f).y + 10f);
                Place(nativeNotes.gameObject, 540f, y - notesHeight - 8f, 20f, y - 8f);
                y -= notesHeight + 8f;
            }
            string selectedGuid = hull.BlueprintGuid;
            var recruit = RetinueUI.MakeButton(card.transform, L.T("招募所选舰型"),
                280f, 38f, delegate
                {
                    FleetAction((out string r) => SpaceEscortService.Recruit(selectedGuid, out r));
                });
            PlaceTopRight(recruit.gameObject, -16f, -8f);
            RetinueUI.SetInteractable(recruit, available);
            if (!available)
            {
                y = Mathf.Min(y, -416f); // Below both the equipment list and the preview frame.
                var unavailable = RetinueUI.MakeLabelPublic(card.transform,
                    L.F("此舰型当前不可用：{0}", reason), 16f,
                    VanillaSkin.TextDim, TextAlignmentOptions.TopLeft);
                unavailable.textWrappingMode = TextWrappingModes.Normal;
                unavailable.overflowMode = TextOverflowModes.Overflow;
                float warningHeight = Mathf.Max(44f,
                    unavailable.GetPreferredValues(unavailable.text, 1100f, 0f).y + 8f);
                Place(unavailable.gameObject, 16f, y - warningHeight - 8f, 16f, y - 8f);
                y -= warningHeight + 8f;
            }
            float height = Mathf.Max(436f, -y + 20f);
            var layout = card.GetComponent<LayoutElement>();
            layout.minHeight = layout.preferredHeight = height;
            ((RectTransform)card.transform).sizeDelta = new Vector2(0f, height);
        }

        private static void ShowRecruitText(TextMeshProUGUI text)
        {
            // The vanilla CJK font's line metrics exceed these compact row bounds.
            // TMP's default truncation clears the whole line; mirror the roster labels.
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
        }

        private static void AddScrollbar(ScrollRect scroll)
        {
            var track = RetinueUI.NewUI("FleetScrollbar", scroll.transform);
            var trackRT = (RectTransform)track.transform;
            trackRT.anchorMin = new Vector2(1f, 0f);
            trackRT.anchorMax = new Vector2(1f, 1f);
            trackRT.offsetMin = new Vector2(-14f, 0f);
            trackRT.offsetMax = Vector2.zero;
            var background = track.AddComponent<Image>();
            background.color = new Color(0.10f, 0.12f, 0.10f, 1f);
            var handle = RetinueUI.NewUI("Handle", track.transform);
            RetinueUI.StretchPadPublic(handle, 2f, 2f);
            var handleImage = handle.AddComponent<Image>();
            handleImage.color = VanillaSkin.Gold;
            var bar = track.AddComponent<Scrollbar>();
            bar.handleRect = (RectTransform)handle.transform;
            bar.targetGraphic = handleImage;
            bar.direction = Scrollbar.Direction.BottomToTop;
            scroll.verticalScrollbar = bar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
            var viewport = scroll.viewport;
            viewport.offsetMax = new Vector2(-20f, viewport.offsetMax.y);
        }

        private delegate bool FleetOperation(out string result);

        private static bool FleetAction(FleetOperation operation)
        {
            string result = "";
            bool ok = operation != null && operation(out result);
            _replyText = result ?? "";
            if (ok) Main.Log("[海战舰队] " + _replyText);
            else Main.LogError("[海战舰队] " + _replyText);
            Refresh();
            return ok;
        }

        private static void MakeFleetCard(SpaceFleetEntry entry, string gameId)
        {
            var hull = SpaceFleetCatalog.Find(entry.BlueprintGuid);
            if (hull == null) return;
            string draftKey = (gameId ?? "") + "|" + entry.Id;
            bool equipmentExpanded = ExpandedEquipmentCards.Contains(draftKey);
            int bonusRows = 0;
            foreach (string field in new[] { "bs", "ns", "br", "nr" })
                if (SpaceEscortService.BonusLimit(hull, field) > 0) bonusRows++;
            int loadoutRows = 0;
            int expandedRows = 0;
            if (equipmentExpanded)
                foreach (var slot in hull.Slots)
                    if (SpaceFleetCatalog.IsOpenSlot(slot))
                    {
                        loadoutRows++;
                        if (string.Equals(_expandedLoadout, entry.Id + "|" + slot.Key,
                            StringComparison.Ordinal))
                            expandedRows += Math.Max(1,
                                SpaceEscortService.ItemOptions(entry, slot).Count);
                    }
            float height = 178f + (loadoutRows + bonusRows + expandedRows) * 42f;
            GameObject card = MakeCard("Fleet_" + entry.Id, height);

            var cost = FleetBudget.Cost(entry);
            var title = RetinueUI.MakeLabelPublic(card.transform,
                L.F("<b>{0}</b>　{1}　　总占用 <color=#c6a24e>{2} PF</color>",
                    string.IsNullOrEmpty(entry.Name) ? entry.Id : entry.Name,
                    SpaceFleetCatalog.DisplayName(hull), cost.Total),
                19f, VanillaSkin.Text, TextAlignmentOptions.Left);
            Place(title.gameObject, 16f, -38f, 16f, -6f);
            title.overflowMode = TextOverflowModes.Overflow;
            var formula = RetinueUI.MakeLabelPublic(card.transform,
                CostFormula(entry, hull, cost), 15f, VanillaSkin.TextDim,
                TextAlignmentOptions.Left);
            Place(formula.gameObject, 16f, -72f, 16f, -40f);

            string draft;
            if (!FleetNameDrafts.TryGetValue(draftKey, out draft))
                FleetNameDrafts[draftKey] = draft = string.IsNullOrEmpty(entry.Name) ? entry.Id : entry.Name;
            TMP_InputField input = MakeInput(card.transform, draft, 330f, 36f);
            var irt = (RectTransform)input.transform;
            irt.anchorMin = irt.anchorMax = new Vector2(0f, 1f);
            irt.pivot = new Vector2(0f, 1f);
            irt.anchoredPosition = new Vector2(16f, -78f);
            input.onValueChanged.AddListener(value =>
            {
                FleetNameDrafts[draftKey] = value;
                RetinueUI.MarkLayerDirty();
            });

            Button rename = RetinueUI.MakeButton(card.transform, L.T("改名"), 140f, 36f,
                delegate { FleetAction((out string r) => SpaceEscortService.Rename(
                    entry.Id, FleetNameDrafts[draftKey], out r)); });
            PlaceTopLeft(rename.gameObject, 360f, -78f);
            Button dismiss = RetinueUI.MakeButton(card.transform, L.T("遣散"), 140f, 36f,
                delegate
                {
                    if (FleetAction((out string r) => SpaceEscortService.Dismiss(entry.Id, out r)))
                    {
                        FleetNameDrafts.Remove(draftKey);
                        ExpandedEquipmentCards.Remove(draftKey);
                    }
                });
            PlaceTopRight(dismiss.gameObject, -16f, -78f);

            float y = -122f;
            Button equipmentToggle = RetinueUI.MakeButton(card.transform,
                equipmentExpanded ? L.T("收起装备") : L.T("展开装备"), 260f, 34f,
                delegate
                {
                    if (!ExpandedEquipmentCards.Add(draftKey))
                    {
                        ExpandedEquipmentCards.Remove(draftKey);
                        if (_expandedLoadout.StartsWith(entry.Id + "|", StringComparison.Ordinal))
                            _expandedLoadout = "";
                    }
                    Refresh();
                });
            PlaceTopLeft(equipmentToggle.gameObject, 16f, y);
            y -= 42f;
            if (equipmentExpanded)
                foreach (var slot in hull.Slots)
                {
                    if (!SpaceFleetCatalog.IsOpenSlot(slot)) continue;
                    MakeLoadoutRow(card.transform, entry, slot, ref y);
                }
            MakeBonusRowIfAny(card.transform, entry, hull, "bs", L.T("舷炮额外开火"),
                entry.BroadsideExtraShots, ref y);
            MakeBonusRowIfAny(card.transform, entry, hull, "ns", L.T("非舷炮额外开火"),
                entry.NonBroadsideExtraShots, ref y);
            MakeBonusRowIfAny(card.transform, entry, hull, "br", L.T("舷炮额外射程"),
                entry.BroadsideExtraRange, ref y);
            MakeBonusRowIfAny(card.transform, entry, hull, "nr", L.T("非舷炮额外射程"),
                entry.NonBroadsideExtraRange, ref y);
        }

        private static void MakeLoadoutRow(Transform parent, SpaceFleetEntry entry,
            SpaceFleetSlotDef slot, ref float y)
        {
            string key = entry.Id + "|" + slot.Key;
            string current = SpaceEscortService.SelectedItemGuid(entry, slot);
            bool currentIsOriginal = string.Equals(current,
                slot.OriginalItemGuid ?? "", StringComparison.OrdinalIgnoreCase);
            string currentName = SpaceEscortService.ItemDisplayName(current,
                !slot.IsWeapon ? SpaceEscortService.SlotDisplayName(slot) : null);
            if (currentIsOriginal) currentName = L.T("原装 ") + currentName;
            string selectorText = slot.IsWeapon
                ? L.F("{0}　选择武器：{1}", SpaceEscortService.SlotDisplayName(slot), currentName)
                : L.F("{0}　选择组件：{1}", SpaceEscortService.SlotDisplayName(slot), currentName);
            Button selector = RetinueUI.MakeButton(parent, selectorText, 760f, 34f,
                delegate
                {
                    _expandedLoadout = string.Equals(_expandedLoadout, key,
                        StringComparison.Ordinal) ? "" : key;
                    Refresh();
                });
            PlaceTopLeft(selector.gameObject, 16f, y);
            y -= 42f;
            if (!string.Equals(_expandedLoadout, key, StringComparison.Ordinal)) return;

            var options = SpaceEscortService.ItemOptions(entry, slot);
            if (options.Count <= 1 && currentIsOriginal)
            {
                var empty = RetinueUI.MakeLabelPublic(parent,
                    slot.IsWeapon
                        ? L.T("只有原装武器；尚未获得其他适配此槽位且 AI 可用的舰炮。")
                        : L.T("只有原装组件（可为空槽）；尚未获得其他适配此槽位的主组件。"),
                    16f, VanillaSkin.TextDim, TextAlignmentOptions.Left);
                Place(empty.gameObject, 34f, y - 34f, 16f, y);
                y -= 42f;
                return;
            }
            float optionY = y;
            foreach (string option in options)
            {
                string selected = option;
                bool original = string.Equals(option, slot.OriginalItemGuid ?? "",
                    StringComparison.OrdinalIgnoreCase);
                bool active = string.Equals(option, current, StringComparison.OrdinalIgnoreCase);
                string text = (active ? L.T("当前：") : L.T("可选："))
                    + (original ? L.T("原装 ") : "")
                    + SpaceEscortService.ItemDisplayName(option,
                        !slot.IsWeapon ? SpaceEscortService.SlotDisplayName(slot) : null);
                Button choice = RetinueUI.MakeButton(parent, text, 730f, 32f,
                    delegate
                    {
                        _expandedLoadout = "";
                        FleetAction((out string r) => SpaceEscortService.SelectItem(
                            entry.Id, slot.Key, selected, out r));
                    });
                PlaceTopLeft(choice.gameObject, 34f, optionY);
                optionY -= 42f;
                y -= 42f;
            }
        }

        private static string CostFormula(SpaceFleetEntry entry, SpaceFleetHullDef hull,
            FleetBudgetBreakdown cost)
        {
            int refitSlots = SpaceFleetCatalog.ModifiedOpenSlotCount(entry);
            int shotLevels = hull.BroadsideWeaponSlots * entry.BroadsideExtraShots
                + hull.NonBroadsideWeaponSlots * entry.NonBroadsideExtraShots;
            int rangeLevels = hull.BroadsideWeaponSlots * entry.BroadsideExtraRange
                + hull.NonBroadsideWeaponSlots * entry.NonBroadsideExtraRange;
            return L.F("基础舰体 {0} PF　+　换装 {1} 槽 × {2} = {3}　+　额外开火累计 {4} 次 × {5} = {6}　+　额外射程累计 {7} 格 × {8} = {9}",
                cost.Hull, refitSlots, FleetBudget.RefitPerSlot(), cost.Refit,
                shotLevels, FleetBudget.PerShot(), cost.Shots,
                rangeLevels, FleetBudget.PerRange(), cost.Range);
        }

        private static void MakeBonusRowIfAny(Transform parent, SpaceFleetEntry entry,
            SpaceFleetHullDef hull, string field, string label, int value, ref float y)
        {
            int limit = SpaceEscortService.BonusLimit(hull, field);
            if (limit <= 0) return;
            bool broadside = field == "bs" || field == "br";
            bool shots = field == "bs" || field == "ns";
            int affected = broadside ? hull.BroadsideWeaponSlots : hull.NonBroadsideWeaponSlots;
            int unitPf = affected * (shots ? FleetBudget.PerShot() : FleetBudget.PerRange());
            var text = RetinueUI.MakeLabelPublic(parent,
                L.F("{0}　等级 {1} / {2}　影响 {3} 门炮；每 +1 占用 {4} PF",
                    label, value, limit, affected, unitPf), 17f,
                VanillaSkin.Text, TextAlignmentOptions.Left);
            Place(text.gameObject, 16f, y - 34f, 330f, y);
            Button minus = RetinueUI.MakeButton(parent, L.T("降低"), 140f, 32f,
                delegate { FleetAction((out string r) => SpaceEscortService.AdjustBonus(
                    entry.Id, field, -1, out r)); });
            PlaceTopRight(minus.gameObject, -170f, y);
            Button plus = RetinueUI.MakeButton(parent, L.T("提高"), 140f, 32f,
                delegate { FleetAction((out string r) => SpaceEscortService.AdjustBonus(
                    entry.Id, field, 1, out r)); });
            PlaceTopRight(plus.gameObject, -16f, y);
            y -= 42f;
        }

        private static GameObject MakeCard(string name, float height)
        {
            GameObject card = RetinueUI.NewUI(name, _content);
            RetinueUI.PaintPanel(card.AddComponent<Image>(), null, VanillaSkin.RowBg);
            var layout = card.AddComponent<LayoutElement>();
            layout.minHeight = height; layout.preferredHeight = height;
            ((RectTransform)card.transform).sizeDelta = new Vector2(0f, height);
            return card;
        }

        private static void MakeInfoRow(string text)
        {
            GameObject card = MakeCard("FleetInfo", 54f);
            var label = RetinueUI.MakeLabelPublic(card.transform, text, 18f,
                VanillaSkin.TextDim, TextAlignmentOptions.Left);
            RetinueUI.StretchPadPublic(label.gameObject, 16f, 0f);
        }

        private static TMP_InputField MakeInput(Transform parent, string text, float width, float height)
        {
            GameObject go = RetinueUI.NewUI("FleetNameInput", parent);
            var image = go.AddComponent<Image>();
            RetinueUI.PaintPanel(image, null, new Color(0.08f, 0.09f, 0.08f, 1f));
            var input = go.AddComponent<TMP_InputField>();
            input.targetGraphic = image;
            input.lineType = TMP_InputField.LineType.SingleLine;

            GameObject viewport = RetinueUI.NewUI("Viewport", go.transform);
            RetinueUI.StretchPadPublic(viewport, 8f, 0f);
            viewport.AddComponent<RectMask2D>();
            var value = RetinueUI.MakeLabelPublic(viewport.transform, text ?? "", 18f,
                VanillaSkin.Text, TextAlignmentOptions.Left);
            RetinueUI.StretchPadPublic(value.gameObject, 2f, 0f);
            value.raycastTarget = true;
            input.textViewport = (RectTransform)viewport.transform;
            input.textComponent = value;
            input.text = text ?? "";
            input.characterLimit = 64;
            ((RectTransform)go.transform).sizeDelta = new Vector2(width, height);
            return input;
        }

        private static void Place(GameObject go, float left, float bottom, float right, float top)
        {
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, top);
        }

        private static void PlaceTopLeft(GameObject go, float x, float y)
        {
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, y);
        }

        private static void PlaceTopRight(GameObject go, float x, float y)
        {
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(x, y);
        }

    }
}
