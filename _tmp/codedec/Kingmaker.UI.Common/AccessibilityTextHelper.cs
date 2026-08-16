using System;
using System.Collections.Generic;
using System.Linq;
using Kingmaker.Settings;
using TMPro;

namespace Kingmaker.UI.Common;

public class AccessibilityTextHelper : IDisposable
{
	private class TextData
	{
		public readonly float TextInitialSize;

		public readonly float AutoSizeMin;

		public readonly float AutoSizeMax;

		public TextData(TMP_Text text)
		{
			TextInitialSize = text.fontSize;
			AutoSizeMin = text.fontSizeMin;
			AutoSizeMax = text.fontSizeMax;
		}

		public TextData(float textInitialSize, float autoSizeMin, float autoSizeMax)
		{
			TextInitialSize = textInitialSize;
			AutoSizeMin = autoSizeMin;
			AutoSizeMax = autoSizeMax;
		}
	}

	private readonly Dictionary<TMP_Text, TextData> m_TextToInitSizeMap;

	private bool m_IsUpdated;

	public AccessibilityTextHelper(params TMP_Text[] texts)
	{
		m_TextToInitSizeMap = new Dictionary<TMP_Text, TextData>();
		foreach (TMP_Text tMP_Text in texts)
		{
			if (!(tMP_Text == null))
			{
				m_TextToInitSizeMap.TryAdd(tMP_Text, new TextData(tMP_Text));
			}
		}
	}

	public void AppendTexts(params TMP_Text[] texts)
	{
		List<KeyValuePair<TMP_Text, TextData>> list = new List<KeyValuePair<TMP_Text, TextData>>();
		foreach (TMP_Text tMP_Text in texts)
		{
			if (!(tMP_Text == null))
			{
				KeyValuePair<TMP_Text, TextData> item = new KeyValuePair<TMP_Text, TextData>(tMP_Text, new TextData(tMP_Text));
				if (m_TextToInitSizeMap.TryAdd(item.Key, item.Value))
				{
					list.Add(item);
				}
			}
		}
		if (m_IsUpdated)
		{
			UpdateTextInternal(list);
		}
	}

	public void UpdateTextSize()
	{
		if (!m_IsUpdated)
		{
			UpdateTextInternal(m_TextToInitSizeMap.ToList());
			m_IsUpdated = true;
		}
	}

	private void UpdateTextInternal(List<KeyValuePair<TMP_Text, TextData>> textsToUpdate)
	{
		float fontSizeMultiplier = SettingsRoot.Accessiability.FontSizeMultiplier;
		foreach (var (tMP_Text2, textData2) in textsToUpdate)
		{
			tMP_Text2.fontSizeMax = textData2.AutoSizeMax * fontSizeMultiplier;
			tMP_Text2.fontSizeMin = textData2.AutoSizeMin * fontSizeMultiplier;
			tMP_Text2.fontSize = textData2.TextInitialSize * fontSizeMultiplier;
		}
	}

	public void Dispose()
	{
		m_IsUpdated = false;
		foreach (var (tMP_Text2, textData2) in m_TextToInitSizeMap)
		{
			tMP_Text2.fontSizeMax = textData2.AutoSizeMax;
			tMP_Text2.fontSizeMin = textData2.AutoSizeMin;
			tMP_Text2.fontSize = textData2.TextInitialSize;
		}
	}
}
