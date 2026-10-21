#!/usr/bin/env python3
"""Füllt eine leere vB-4.2-Datenbank (nur Schema) mit Testdaten für die lokale Entwicklung.
Aufruf: python3 seed_testdb.py | mysql -uroot forum
Passwörter: admin / admin123, MaxM / test1234, Kathi_R / test1234 (vB-md5-Format)."""
import hashlib, time, random, struct, zlib, base64

def md5(b): return hashlib.md5(b).hexdigest()
def vbpw(pw, salt): return md5((md5(pw.encode('latin-1')) + salt).encode('latin-1'))
def q(s): return "'" + str(s).replace("\\", "\\\\").replace("'", "\\'") + "'"

def png(w, h, rgb):
    raw = b''.join(b'\x00' + bytes(rgb) * w for _ in range(h))
    def chunk(t, d): return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(raw)) + chunk(b'IEND', b'')

now = int(time.time())
out = []
P = out.append
P("SET NAMES utf8mb4;")

FP = dict(canview=1, canviewothers=2, cansearch=4, canemail=8, canpostnew=16, canreplyown=32, canreplyothers=64,
          caneditpost=128, candeletepost=256, candeletethread=512, canopenclose=1024, canmove=2048, cangetattachment=4096,
          canpostattachment=8192, canpostpoll=16384, canvote=32768, canthreadrate=65536, followforummoderation=131072,
          canseedelnotice=262144, canviewthreads=524288)
guest = FP['canview'] | FP['canviewothers'] | FP['cansearch'] | FP['cangetattachment'] | FP['canviewthreads']
reg = guest | FP['canemail'] | FP['canpostnew'] | FP['canreplyown'] | FP['canreplyothers'] | FP['caneditpost'] | FP['candeletepost'] | FP['canpostattachment'] | FP['canpostpoll'] | FP['canvote'] | FP['canthreadrate'] | FP['followforummoderation']
allp = sum(FP.values())
groups = [
    (1, 'Unregistriert / Nicht angemeldet', 'Gast', guest, 0),
    (2, 'Registrierte Benutzer', 'Mitglied', reg, 0),
    (3, 'Benutzer, die ihre E-Mail-Adresse noch nicht bestätigt haben', '', guest, 0),
    (4, '(COPPA) Benutzer, die auf Bestätigung warten', '', guest, 0),
    (5, 'Super-Moderatoren', 'Super-Moderator', allp, 1),
    (6, 'Administratoren', 'Administrator', allp, 3),
    (7, 'Moderatoren', 'Moderator', allp, 1),
    (8, 'Gesperrte Benutzer', 'Gesperrt', 0, 0),
]
for gid, title, ut, fp, ap in groups:
    P(f"INSERT INTO vb_usergroup (usergroupid,title,usertitle,forumpermissions,adminpermissions,genericoptions,pmquota) VALUES ({gid},{q(title)},{q(ut)},{fp},{ap},{0 if gid==8 else 32},50);")

# Forenbaum: (id, parent, title, desc, isCategory, order)
forums = [
    (1, -1, 'Allgemeines', '', True, 10),
    (2, 1, 'Ankündigungen', 'Neuigkeiten rund um das Forum.', False, 10),
    (3, 1, 'Plauderecke', 'Alles, was nirgendwo sonst hinpasst.', False, 20),
    (4, -1, 'Technik', '', True, 20),
    (5, 4, 'Hardware', 'PCs, Komponenten, Peripherie und Kaufberatung.', False, 10),
    (6, 5, 'Grafikkarten', '', False, 10),
    (7, 5, 'Mainboards & CPUs', '', False, 20),
    (8, 4, 'Software', 'Betriebssysteme & Programmierung.', False, 20),
    (9, -1, 'Intern', '', True, 30),
    (10, 9, 'Moderatoren', 'Nur für das Team.', False, 10),
]
parents = {f[0]: f[1] for f in forums}
def plist(fid):
    l = [fid]
    while parents[l[-1]] != -1: l.append(parents[l[-1]])
    return ','.join(map(str, l)) + ',-1'
def clist(fid):
    res = [fid]
    for f in forums:
        if f[1] == fid: res += [int(x) for x in clist(f[0]).split(',')[:-1]]
    return ','.join(map(str, res)) + ',-1'
for fid, par, title, desc, cat, order in forums:
    opts = 1 | 2 | 64 | 128 | 512 | 1024 | 4096 | 16384 | 65536 | (0 if cat else 4)
    tt = title.replace('&', '&amp;')
    P(f"INSERT INTO vb_forum (forumid,title,title_clean,description,description_clean,options,displayorder,parentid,parentlist,childlist) VALUES ({fid},{q(tt)},{q(title)},{q(desc)},{q(desc)},{opts},{order},{par},{q(plist(fid))},{q(clist(fid))});")
# Intern: Gäste/Mitglieder sehen nichts
for gid in (1, 2, 3):
    P(f"INSERT INTO vb_forumpermission (forumid,usergroupid,forumpermissions) VALUES (9,{gid},0);")

users = [
    (1, 'admin', 6, 'admin@example.org', 'admin123'),
    (2, 'MaxM', 2, 'max@example.org', 'test1234'),
    (3, 'Kathi_R', 2, 'kathi@example.org', 'test1234'),
    (4, 'devpaul', 7, 'paul@example.org', 'test1234'),
    (5, 'Grüße&Co', 2, 'gruesse@example.org', 'pässwort'),
    (6, 'spambot42', 2, 'spam@example.org', 'xyz'),
]
for uid, name, gid, mail, pw in users:
    salt = ''.join(random.choice('abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!$%') for _ in range(30))
    joined = now - random.randint(200, 4000) * 86400
    P(f"INSERT INTO vb_user (userid,usergroupid,username,password,salt,email,joindate,lastvisit,lastactivity,usertitle,fbuserid,fbname,fbaccesstoken,options) VALUES ({uid},{gid},{q(name.replace('&','&amp;'))},{q(vbpw(pw,salt))},{q(salt)},{q(mail)},{joined},{now-86400},{now-3600},'',{q('')},{q('')},{q('')},45108311);")
    P(f"INSERT INTO vb_userfield (userid,field2,field3,field5) VALUES ({uid},'Albbruck','','');")
    P(f"INSERT INTO vb_usertextfield (userid,signature) VALUES ({uid},{q('Gruß, '+name if uid in (2,3) else '')});")
P("INSERT INTO vb_moderator (userid,forumid,permissions) VALUES (4,5,4294967295);")
P("INSERT INTO vb_smilie (title,smilietext,smiliepath,displayorder) VALUES ('Lächeln',':)','images/smilies/smile.png',1),('Zwinkern',';)','images/smilies/wink.png',2),('Lachen',':D','images/smilies/biggrin.png',3);")
P("INSERT INTO vb_package (packageid,productid,class) VALUES (1,'vbulletin','vBForum');")
P("INSERT INTO vb_contenttype (contenttypeid,class,packageid,canattach) VALUES (1,'Post',1,'1');")
P("INSERT INTO vb_setting (varname,value) VALUES ('attachfile',''),('usefileavatar',''),('templateversion','4.2.5');")
P("INSERT INTO vb_phrase (languageid,varname,fieldname,text,product) VALUES (0,'field2_title','cprofilefield','Wohnort','vbulletin'),(0,'field3_title','cprofilefield','Interessen','vbulletin');")

texts = [
    "Hallo zusammen,\n\nseit dem [b]BIOS-Update[/b] auf 2.40 drehen die Lüfter hoch. Preis war 5 &#8364;. &#128512;\n\n[ATTACH=CONFIG]1[/ATTACH]",
    "[quote=Kathi_R;{p}]seit dem BIOS-Update drehen die Lüfter hoch[/quote]\nBekannter Fehler. Prüf mal:\n[code]sensors | grep -i fan[/code]",
    "Danke :) Hat geklappt. Siehe auch https://example.org/doku",
    "[list]\n[*]Punkt eins\n[*]Punkt [i]zwei[/i]\n[/list]\n[img]http://example.org/bild.jpg[/img]",
]
postid = 0; threadid = 0
fcount = {}
for fid in (2, 3, 5, 6, 7, 8, 10):
    for t in range(1, 26 if fid == 7 else 4):
        threadid += 1
        start = now - random.randint(2, 900) * 3600 - t * 7200
        title = f"Thema {threadid}: Lüftersteuerung & BIOS" if t == 1 else f"Testthema Nr. {threadid} – „Anführungszeichen“"
        nposts = 4 if t == 1 else random.randint(1, 3)
        first = postid + 1
        last_user = None; last_time = start
        for i in range(nposts):
            postid += 1
            uid = [3, 4, 2, 1][i % 4] if t == 1 else random.choice([2, 3, 4, 5])
            uname = [u[1] for u in users if u[0] == uid][0].replace('&', '&amp;')
            txt = texts[i % 4].replace('{p}', str(first))
            last_time = start + i * 1800
            P(f"INSERT INTO vb_post (postid,threadid,username,userid,title,dateline,pagetext,allowsmilie,showsignature,ipaddress,visible,attach) VALUES ({postid},{threadid},{q(uname)},{uid},'',{last_time},{q(txt)},1,1,'127.0.0.1',1,{1 if (t==1 and i==0) else 0});")
            last_user = (uid, uname)
            if i == 0: starter = (uid, uname)
        vis = 1
        sticky = 1 if (fid == 7 and t == 2) else 0
        P(f"INSERT INTO vb_thread (threadid,title,firstpostid,lastpostid,lastpost,forumid,open,replycount,postusername,postuserid,lastposter,lastposterid,dateline,views,visible,sticky,attach) VALUES ({threadid},{q(title.replace('&','&amp;'))},{first},{postid},{last_time},{fid},1,{nposts-1},{q(starter[1])},{starter[0]},{q(last_user[1])},{last_user[0]},{start},{random.randint(10,900)},{vis},{sticky},{1 if t==1 else 0});")

P("""UPDATE vb_forum f JOIN (SELECT forumid, COUNT(*) tc, SUM(replycount+1) pc FROM vb_thread GROUP BY forumid) x ON x.forumid=f.forumid
     SET f.threadcount=x.tc, f.replycount=x.pc;""")
P("""UPDATE vb_forum f JOIN vb_thread t ON t.threadid=(SELECT threadid FROM vb_thread WHERE forumid=f.forumid ORDER BY lastpost DESC LIMIT 1)
     SET f.lastpost=t.lastpost, f.lastpostid=t.lastpostid, f.lastposter=t.lastposter, f.lastposterid=t.lastposterid, f.lastthread=t.title, f.lastthreadid=t.threadid;""")

# Ein moderierter Beitrag und ein gelöschtes Thema
P(f"UPDATE vb_post SET visible=0 WHERE postid={postid};")
P(f"INSERT INTO vb_moderation (primaryid,type,dateline) VALUES ({postid},'reply',{now});")

img = png(640, 360, (60, 110, 200)); thumb = png(160, 90, (60, 110, 200))
P(f"INSERT INTO vb_filedata (filedataid,userid,dateline,filedata,filesize,filehash,thumbnail,thumbnail_filesize,extension,width,height,thumbnail_width,thumbnail_height,refcount) VALUES (1,3,{now},0x{img.hex()},{len(img)},{q(md5(img))},0x{thumb.hex()},{len(thumb)},'png',640,360,160,90,1);")
P(f"INSERT INTO vb_attachment (attachmentid,contenttypeid,contentid,userid,dateline,filedataid,state,filename) VALUES (1,1,1,3,{now},1,'visible','luefterkurve.png');")
P("UPDATE vb_user u SET posts=(SELECT COUNT(*) FROM vb_post p WHERE p.userid=u.userid);")
P(f"INSERT INTO vb_pmtext (pmtextid,fromuserid,fromusername,title,message,touserarray,dateline,allowsmilie) VALUES (1,4,'devpaul','Re: Verkauf Grafikkarte','Hi Max, ist die Karte noch da?','a:1:{{s:2:\"cc\";a:1:{{i:2;s:4:\"MaxM\";}}}}',{now-7200},1);")
P("INSERT INTO vb_pm (pmtextid,userid,folderid,messageread) VALUES (1,2,0,0),(1,4,-1,1);")
P("UPDATE vb_user SET pmtotal=1, pmunread=1 WHERE userid=2;")
P("INSERT INTO vb_subscribethread (userid,threadid,emailupdate) VALUES (2,1,1);")
print('\n'.join(out))
