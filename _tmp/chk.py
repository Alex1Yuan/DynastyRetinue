import json,sys
p=r"H:\SteamLibrary\steamapps\common\Warhammer 40,000 Rogue Trader\Bundles\locationlist.json"
d=json.load(open(p,encoding='utf-8'))
g=d["m_Guids"]; b=d["m_Bundles"]
m=dict(zip(g,b))
print("entries",len(g),"unique",len(m))
cand={
"a6bcda106bf8fd44da4286ee04a3ad8f":"Sword frigate (player)",
"26e3688a99a9eed44baa2e19e16be1a4":"Falchion frigate (player)",
"31da3f04de39e5446b16641deb3be42d":"Firestorm frigate (player)",
"67017c4dd1d5c1c40979ce2fc1cd38b2":"ImperialCruiser10Named",
"10de1ae75122ba243b423194534e5182":"ChaosCruiser5",
"8c34d0a2f4987134c8a625612476e22d":"OrkCruiser10/PirateCruiser7",
"e18691bc8276691408852ec91c909c42":"DrukhariCruiser6",
"82d6449f24d12b94eb8d87225f10de32":"Aeldari cruiser?",
"0da2b98b8cef1b8498dad3ecb12cfb6b":"ChaosGrand10",
"0ea91ee80d7b01a44b3cad74efbc8a72":"ImperialTransport3",
"5ffb23a1b630d1d46b222e70aa56fb8c":"DLC1_ChaosTransport",
"6f3f035a080710949bd40b7a1af533eb":"Globalmap_Starship",
"0f539babafb47fe4586b719d02aff7c4":"ENV guess (suspected Mobs faction bp)",
}
for k,v in cand.items():
    print(("HIT  %-10s"%m[k] if k in m else "MISS      "), k, v)
