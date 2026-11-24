# mini_screenshot

Minimalista képernyőkép-készítő Windowsra. A háttérben fut, és a **PrtSc** gombra indul (Win+PrtSc-re is). RDP alatt is működik.

## Letöltés és indítás

1. Töltsd le a `Screen.exe`-t a [Releases](https://github.com/erdossyaron/mini_screenshot/releases) oldalról. Csak ez az egy fájl kell, .NET sem.
2. Indítsd el. Feljön a **telepítő varázsló**:
   - **Hova mentse a képeket?** Alapból: `Képek\Képernyőképek`. A **Tallózás…** gombbal másikat választhatsz.
   - **Indítás a Windows-zal:** alapból be van kapcsolva.
3. Kattints a **Telepítés** gombra. Az óra mellett megjelenik egy kis ikon és egy értesítés. Innentől a PrtSc gomb ezt az appot indítja.

A telepítő az appot ide másolja: `%LOCALAPPDATA%\Programs\mini_screenshot\Screen.exe`. A letöltött fájl ezután nyugodtan törölhető. Rendszergazdai jog nem kell.

Ha az app már fut, és újra elindítod, egy értesítés jelzi, hogy már fut.

A mentési mappát később is módosíthatod. Kattints a **Másol & Ment** gomb melletti **mappa ikonra**, vagy a tálcaikonon jobb gombbal válaszd a **Mentési mappa…** pontot. A mappa ikon fölé állva látod, éppen hova ment.

Az automatikus indulást a tálcaikonon jobb gombbal, az **Indítás a Windows-zal** ponttal kapcsolhatod ki és be. Ha kikapcsolod, az app nem kapcsolja vissza.

**Frissítés:** töltsd le az új `Screen.exe`-t, indítsd el, és a varázslóban kattints a **Frissítés** gombra. A futó régi példányt a varázsló maga állítja le és cseréli le.

**Eltávolítás:** tálcaikon → vedd ki a pipát az **Indítás a Windows-zal** elől → Kilépés. Utána töröld a `%LOCALAPPDATA%\Programs\mini_screenshot` mappát.

> A Windows első indításkor figyelmeztethet („A Windows megvédte a számítógépét”), mert az exe nincs digitálisan aláírva. Ilyenkor kattints a **További információ**, majd a **Futtatás mindenképp** gombra.

## Használat

| Művelet | Hogyan |
|---|---|
| Képernyő befagyasztása | **PrtSc** |
| Vágás | húzd a sarkokat vagy az éleket, vagy kattints kétszer a két sarokpontra |
| Fix képarány | kattints a méretkijelzőre, és válassz arányt (16:9, 4:3, 1:1 …) |
| Vágólapra másolás | **Másolás** gomb vagy **Enter** |
| Mentés PNG-be is | a Másolás melletti **kapcsoló** (floppy ikon) – bekapcsolva a Másolás menti is a képet |
| Hosszú kép | **Hosszú kép** gomb a méretkijelző mellett (lásd lent) |
| Korábbi képek | **óra ikon** a méretkijelző előtt (lásd lent) |
| Visszavonás | **Ctrl+Z** – mindig az utolsó mozzanat (rajzolás, mozgatás, vágás, szín, törlés, gépelés…) |
| Mégse | **Esc** vagy jobb klikk |

Semmi nem kerül a vágólapra vagy a lemezre, amíg a **Másolás** gombra nem kattintasz. Ha a mentés kapcsoló be van kapcsolva, a kép PNG-ként a választott mappába is mentődik. A kapcsoló állását az app megjegyzi. Utána a jobb alsó sarokban megjelenik egy előnézet: „Másolva & mentve” vagy „Vágólapra másolva”. Mentett képnél rákattintva megnyílik a fájl az Intézőben. Az **X**-szel bezárhatod.

Ha több monitorod van, mindegyik befagy, és annak a kijelzőnek a vágását menti, ahol a gombra kattintasz.

### Hosszú kép (görgetve rögzítés)

1. Jelöld ki a tartalmat, és kattints a **Hosszú kép** gombra.
2. Az app maga görgeti lefelé a kijelölés alatti ablakot, és összefűzi a képet. Jobb oldalt látszik az előnézet és a magasság.
3. Az oldal végén magától leáll, vagy a **Kész (Enter)** gombbal te állítod le. **Mégse: Esc.**
4. A hosszú kép a szerkesztőben nyílik meg: görgővel görgethető, a megszokott vágóval vághatod, és minden eszközzel rajzolhatsz rá.

A rögzített részeket (fejléc, oldalsáv, lebegő beviteli doboz, lábléc) az app felismeri: a fejléc egyszer felül, a lábléc egyszer alul jelenik meg, az oldalsáv csak az első képernyő magasságáig, a görgetett tartalom pedig végig „lefolyik”. Ha valami nem jó, a napló itt van: `%LOCALAPPDATA%\Programs\mini_screenshot\hosszu_kep.log`.

### Korábbi képek (előzmények)

Minden **Másolás** után az app eltárolja a képet az előzmények közé. Az eredeti képernyőkép, a vágás és a rajzolt objektumok külön-külön megmaradnak.

1. Kattints az **óra ikonra** a méretkijelző előtt. Feljön egy rács a legutóbbi képek kicsinyített előnézetével és az időpontjukkal.
2. Kattints egy képre. Megnyílik a szerkesztőben, és ugyanott folytathatod: a vágás állítható, az objektumok mozgathatók, szerkeszthetők, törölhetők.
3. A Másolással a módosított kép új előzményként kerül a listába.

A hosszú képek és a másik monitoron készült képek a görgethető szerkesztőben nyílnak meg.

Az előzmények itt vannak: `%LOCALAPPDATA%\Programs\mini_screenshot\elozmenyek`. Mindet törölheted a tálcaikonon jobb gombbal, a **Korábbi képek törlése** ponttal. A mentett PNG-fájlok ettől megmaradnak.

### Eszközök

| Eszköz | Billentyű | Megjegyzés |
|---|---|---|
| Vágás | C | alapértelmezett |
| Elmosás | B | a téglalap alatti rész olvashatatlan lesz; inverz módban minden el van mosva, csak a téglalap éles (fókusz) |
| Ovál | O | Shift: kör |
| Téglalap | R | Shift: négyzet |
| Vonal / nyíl | L | nyíl az elején vagy a végén, irány cseréje, Shift: 45° |
| Ceruza | P | szabadkézi vonal |
| Szöveg | T | betűméret, háttérszín, átlátszóság; dupla katt: szerkesztés |
| Kéz (kijelölés) | H | kattints egy objektumra: kijelöli, és átvált az eszközére |

A szín piros, fehér vagy fekete lehet.

### Objektumok szerkesztése

- A frissen rajzolt vagy kijelölt objektum **bárhol megfogva mozgatható**.
- A **fehér fogópontokon** méretezhető.
- **Egy kattintás bárhova** leveszi a fókuszt. Utána húzással új objektumot rajzolsz, kattintással kijelölsz egyet.
- A kijelölt objektum mellett egy **kis panel** jelenik meg: rétegsorrend (leghátra, hátrébb, előrébb, legelőre) és törlés.
- **Delete:** a kijelölt objektum törlése. **Ctrl+Z:** az utolsó mozzanat visszavonása, akár többször is.

## Fordítás forrásból

.NET 8 SDK kell hozzá.

```powershell
dotnet build -c Release
```

Önálló, egyfájlos exe:

```powershell
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish
```
