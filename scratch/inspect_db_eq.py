import sqlite3
import json

conn = sqlite3.connect('C:/ProgramData/SteelSeries/GG/apps/engine/db/database.db')
cur = conn.cursor()

print("--- device_game_eq_presets ---")
for r in cur.execute("SELECT * FROM device_game_eq_presets LIMIT 5"):
    print(r)

print("\n--- configurations for Nova 7 ---")
for r in cur.execute("SELECT id, name, device_id, settings FROM configurations WHERE device_id LIKE '%22a1%' OR device_id LIKE '%8865%' OR device_id LIKE '%nova%'"):
    print(r[0], r[1], r[2])
    try:
        s = json.loads(r[3])
        print("Settings keys:", list(s.keys()))
        for k in s:
            if 'eq' in k.lower():
                print("EQ setting:", k, ":", s[k])
    except Exception as e:
        print("JSON err:", e)
