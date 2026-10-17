import hid, time, struct

# Let's find the Nova 7 command interface (0xFFC0, MI_03)
devs = [d for d in hid.enumerate(0x1038, 0x22A1) if d['usage_page'] == 65472]
if not devs:
    print("Nova 7 not found!")
    exit(1)

h = hid.device()
h.open_path(devs[0]['path'])

frequencies = [32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000]

def create_eq_payload(gains_db):
    # Packet 1: 0xA7 Name packet
    # [0x00, 0xA7, connection_type (0x00), preset_type (0x01 Custom), name_bytes...]
    name_pkt = [0x00, 0xA7, 0x00, 0x01]
    name_str = b"Custom\x00"
    name_pkt.extend(name_str)
    name_pkt.extend([0x00] * (65 - len(name_pkt)))
    
    # Packet 2: 0x33 Band data packet
    # [0x00, 0x33, connection_type (0x00), 10 * 6 bytes bands, padding]
    band_pkt = [0x00, 0x33, 0x00]
    for i in range(10):
        freq = frequencies[i]
        filter_type = 1 # Peaking EQ
        gain_float = gains_db[i] * 10.0 # e.g. +6.0 dB -> 60, -6.0 dB -> -60
        gain_byte = int(gain_float) & 0xFF
        q_factor = 1414 # 1.414 * 1000
        
        # 6 bytes: uint16 freq LE, uint8 filter_type, uint8 gain_byte, uint16 q_factor LE
        band_bytes = struct.pack('<HBBH', freq, filter_type, gain_byte, q_factor)
        band_pkt.extend(band_bytes)
        
    band_pkt.extend([0x00] * (65 - len(band_pkt)))
    
    # Packet 3: 0x27 Update Complete trigger packet
    # [0x00, 0x27, 0x00, ...]
    update_pkt = [0x00, 0x27] + [0x00] * 63
    
    # Packet 4: 0x09 Save to flash
    save_pkt = [0x00, 0x09] + [0x00] * 63
    
    return name_pkt, band_pkt, update_pkt, save_pkt

print("Testing EQ packets...")
name_p, band_p, upd_p, save_p = create_eq_payload([6.0, 5.0, 3.0, 0, 0, 0, 0, 0, 4.0, 5.0])

print("Sending 0xA7...")
h.write(name_p)
time.sleep(0.03)

print("Sending 0x33...")
h.write(band_p)
time.sleep(0.03)

print("Sending 0x27 (update complete)...")
h.write(upd_p)
time.sleep(0.03)

print("Sending 0x09 (save to flash)...")
h.write(save_p)
time.sleep(0.5)

h.close()
print("All packets sent successfully!")
