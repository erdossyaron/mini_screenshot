# mini_screenshot

Minimalista képernyőkép-készítő Windowsra. A háttérben fut, és a **PrtSc** gombra indul (Win+PrtSc-re is). RDP alatt is működik.

## Telepítés

1. Töltsd le a `mini_screenshot-Setup-<verzió>.exe`-t a [Releases](https://github.com/erdossyaron/mini_screenshot/releases) oldalról. .NET nem kell hozzá.
2. Indítsd el. Magyar nyelvű telepítő varázsló jön fel. A **Beállítások** oldalon:
   - **Hova mentse a képeket?** Alapból: `Képek\Képernyőképek`. A **Tallózás…** gombbal másikat választhatsz.
   - **Indítás a Windows-zal:** alapból be van kapcsolva.
3. Kattints a **Telepítés**, majd a **Befejezés** gombra. Az óra mellett megjelenik egy kis ikon és egy értesítés. Innentől a PrtSc gomb ezt az appot indítja.

Rendszergazdai jog nem kell. Az app ide kerül: `%LOCALAPPDATA%\Programs\mini_screenshot\Screen.exe`. A Start menüben **mini_screenshot** néven találod, és megjelenik a **Gépház → Alkalmazások** listában is.

**Frissítés:** töltsd le az új telepítőt, és futtasd. A futó régi példányt a telepítő maga állítja le és cseréli le. A korábbi képek és a beállítások (mentési mappa, automatikus indulás) megmaradnak.

**Eltávolítás:** Gépház → Alkalmazások → **mini_screenshot** → Eltávolítás. Az app leáll, és az automatikus indulás is megszűnik. A képeidhez nem nyúl: a mentett PNG-k és a korábbi képek (`%LOCALAPPDATA%\Programs\mini_screenshot\elozmenyek`) megmaradnak, egy újratelepítés után újra elérhetők. Ha végleg nem kellenek, ezeket a mappákat kézzel törölheted.

> A Windows első indításkor figyelmeztethet („A Windows megvédte a számítógépét”), mert a telepítő nincs digitálisan aláírva. Ilyenkor kattints a **További információ**, majd a **Futtatás mindenképp** gombra.

### Telepítő nélkül (egy fájl)

A Releases oldalon a `Screen.exe` is letölthető. Indításkor ez a saját, beépített varázslóját hozza fel (mentési mappa, indítás a Windows-zal), és ugyanoda másolja magát. Frissítéskor a **Frissítés** gombbal cseréli a futó régi példányt. Eltávolítás így: tálcaikon → vedd ki a pipát az **Indítás a Windows-zal** elől → Kilépés, utána töröld a `%LOCALAPPDATA%\Programs\mini_screenshot` mappát.

### Beállítások később

Ha az app már fut, és újra elindítod, egy értesítés jelzi, hogy már fut.

A mentési mappát később is módosíthatod. Kattints a **Másol & Ment** gomb melletti **mappa ikonra**, vagy a tálcaikonon jobb gombbal válaszd a **Mentési mappa…** pontot. A mappa ikon fölé állva látod, éppen hova ment.

Az automatikus indulást a tálcaikonon jobb gombbal, az **Indítás a Windows-zal** ponttal kapcsolhatod ki és be. Ha kikapcsolod, az app nem kapcsolja vissza, és egy frissítés sem.

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

### Hosszú kép (felvétel görgetés vagy húzás közben)

1. Jelöld ki a tartalmat, és kattints a **Hosszú kép** gombra.
2. A fagyasztás eltűnik, a kijelölt terület élő marad, és indul a felvétel (piros **REC** keret). Görgess benne te: egérgörgővel, görgetősávval vagy billentyűvel, akár gyorsan is. Vagy húzd (pásztázd) a területet egérrel, például egy Miro táblát. Az app közben összefűzi a képet. Jobb oldalt látszik az előnézet és a várható méret.
3. A végén kattints a **Stop (Enter)** gombra. **Mégse: Esc.** Kb. 25 000 pixel magasságnál magától leáll.
4. A hosszú kép a szerkesztőben nyílik meg: görgővel görgethető, a megszokott vágóval vághatod, és minden eszközzel rajzolhatsz rá.

**Mód:** alapból **automatikus**. Amíg csak függőleges a mozgás, görgetett kép lesz belőle. Amint oldalirányú mozgás is volt, pásztázott (kétirányú) kép. Az eszköztár **Mód** gombjával kézzel is választhatsz görgetést vagy pásztázást.

Görgetésnél a rögzített részeket (fejléc, oldalsáv, lebegő beviteli doboz, lábléc) az app felismeri: a fejléc egyszer felül, a lábléc egyszer alul jelenik meg, az oldalsáv csak az első képernyő magasságáig, a görgetett tartalom pedig végig „lefolyik”. Pásztázásnál a mozdulatlan eszköztárak kimaradnak.

A széles (pásztázott) kép a szerkesztőben oldalra is görgethető: **Shift + görgő**, vízszintes görgő vagy érintőpad. A **középső egérgombbal** húzva bármelyik eszköznél mozgathatod a nézetet.

Ha valami nem jó, a napló itt van: `%LOCALAPPDATA%\Programs\mini_screenshot\hosszu_kep.log`.

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

## Fejlesztés

.NET 8 SDK kell hozzá.

```powershell
dotnet build -c Release
```

A fejlesztői build (`bin\...`) nem telepíti magát, és nem kapcsol be automatikus indulást. Indítás előtt állítsd le a telepített példányt, mert egyszerre csak egy fut.

Önálló, egyfájlos exe a `dist` mappába (`dist\Screen.exe`):

```powershell
powershell -ExecutionPolicy Bypass -File tools\publish.ps1
```

Telepítő varázsló (Inno Setup 6 kell hozzá: `winget install --id JRSoftware.InnoSetup -e --scope user`):

```powershell
powershell -ExecutionPolicy Bypass -File tools\make-setup.ps1 -Publish
```

A `-Publish` kapcsolóval előbb az exe-t is elkészíti. Eredmény: `release\mini_screenshot-Setup-<verzió>.exe`. Magyar nyelvű varázsló, rendszergazdai jog nélkül telepít, megjelenik a Gépház → Alkalmazások listában. Frissítésnél leállítja a futó példányt és lecseréli, eltávolításkor a képekhez és a beállításokhoz nem nyúl. Forrása: `installer\mini_screenshot.iss`.

- **Verzió:** a `Screen.csproj` `<Version>` eleme adja, a telepítő is ebből veszi. Kiadás előtt ezt léptesd.
- **AppId:** az `.iss` fájlban fix GUID. Ebből ismeri fel a telepítő a frissítést, ne változtasd meg.
- **Telepítési hely:** kötött (`%LOCALAPPDATA%\Programs\mini_screenshot`), mert az app ebből ismeri fel, hogy telepítve van. Ezért nincs mappaválasztó oldal.
- **Kódolás:** a `.ps1` és `.iss` fájlokat BOM-os UTF-8-ban mentsd, különben az ékezetek elromlanak.
