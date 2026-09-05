# Frostpane

Organizează iconițele desktopului în panouri translucide: le aduni pe grupuri, muți și
redimensionezi panoul, îl pliezi la dublu-click pe titlu, iar aranjamentul se ține minte între
sesiuni. În spatele fiecărui panou, wallpaper-ul apare blurat — **inclusiv dacă e animat**, ceea
ce uneltele de acest fel nu reușesc de obicei.

## Instalare

Descarcă `Frostpane-x.y.z-setup.exe` din
[Releases](https://github.com/limburatorul/Frostpane/releases/latest) și rulează-l. Instalarea e
per-utilizator, deci nu cere drepturi de administrator, și nu are nevoie de niciun runtime instalat
separat.

Aplicația pornește fără fereastră proprie: o găsești în tray. Din meniul ei creezi primul panou,
o pornești odată cu Windows, sau verifici actualizările. Panourile se pot crea și direct din meniul
de click dreapta pe desktop.

**Actualizare automată.** La pornire, și apoi din 6 în 6 ore, aplicația întreabă GitHub dacă există
un release mai nou. Dacă da, te întreabă o singură dată dacă vrei să-l instaleze — refuzul se ține
minte pentru versiunea aia. Accepți, ea descarcă installer-ul, îl rulează silențios și repornește
singură. Nu e nevoie de cont sau token: repo-ul e public.

## Din surse

```bash
dotnet run --project "src/Frostpane.App"
```

Pentru executabil și installer (necesită [Inno Setup 6](https://jrsoftware.org/isinfo.php)):

```bash
powershell -File build.ps1
```

Aplicația acceptă și câteva comenzi, folosite de intrările din meniul desktopului și utile pe o
scurtătură: `--new-pane`, `--new-portal`, `--settings`. Dacă aplicația rulează deja, comanda e
predată instanței pornite.

Rezultatul ajunge în `dist/`. Versiunea se schimbă într-un singur loc, `<Version>` din
`src/Frostpane.App/Frostpane.App.csproj`; scriptul o citește de acolo și o pune peste tot.

## Ce face

| | |
|---|---|
| **Panou** | Dreptunghi translucid cu titlu. Trage de titlu ca să-l muți, de margini ca să-l redimensionezi. |
| **Pliere** | Dublu-click pe bară pliază și depliază, animat. Dublu-click pe **nume** redenumește. |
| **Lipire de margine** | Tras pe marginea de sus sau de jos a ecranului, panoul se lipește și se pliază singur. Tras înapoi, se depliază. |
| **Peek la hover** | Cu mouse-ul pe un panou pliat, acesta se deschide cât stai pe el; se închide la loc când pleci. |
| **Setări** | Blur pornit/oprit, moliciunea și luminozitatea blur-ului, opacitatea fundalului, intensitatea și culoarea tentei, mărimea iconițelor, peek, pornire la logon. Din tray sau din click dreapta pe panou. |
| **Adopție** | Orice iconiță pe care o tragi pe desktop peste un panou intră în el. Shell-ul o mută, noi o revendicăm. |
| **Lansare** | Dublu-click pe o iconiță dintr-un panou o deschide, cu verbul implicit al shell-ului. |
| **Meniu contextual** | Click dreapta pe o iconiță: Deschide / Redenumește / Șterge / Proprietăți — dialogurile reale ale Windows-ului. |
| **Portal** | Un panou care oglindește un folder de pe disc, cu miniaturi reale, actualizat automat. |
| **Blur** | Fundalul fiecărui panou e o probă blurată a wallpaper-ului, actualizată ~10 ori pe secundă. |
| **Meniu pe desktop** | Click dreapta pe desktop → **Panou nou aici** / **Portal nou aici**. Pe Windows 11 sunt sub „Show more options" (sau direct la Shift+click dreapta): meniul compact acceptă doar handler-e din pachete MSIX semnate. |
| **Persistență** | `%APPDATA%\Frostpane\layout.json`. |

## Blur-ul pe un wallpaper întunecat

O probă blurată a unui wallpaper aproape negru este ea însăși aproape neagră, deci invizibilă — iar
panoul arată ca o cutie. Nu e o defecțiune, e aritmetică: media pe blocuri a unor glife rare și
subțiri dă negru. Windows rezolvă asta în acrylic printr-un „luminosity blend", care ridică proba
înainte de a o afișa.

Frostpane face la fel, cu două reglaje:

- **Blur brightness** ridică proba spre alb, proporțional cu cât de întunecat e fiecare pixel, deci
  luminile rămân neatinse. Ăsta e reglajul care transformă cutia neagră în sticlă mată.
- **Background opacity** sub 100% lasă wallpaper-ul viu, neblurat, să treacă prin panou. E un efect
  diferit: nu sticlă mată, ci geam.

Implicit: opacitate 100, luminozitate 24, moliciune 4 — sticlă mată.

**Tenta** e limitată la 60%%, intenționat: o tentă care acoperă proba blurată transformă panoul
înapoi într-un dreptunghi plat, adică exact problema pe care blur-ul o rezolvă.

## Cum e construit

Trei probleme au dictat arhitectura, în ordinea în care au apărut.

**1. Cine mută iconițele.** Pozițiile iconițelor de desktop se citesc și se scriu prin
`IFolderView2`, obținut din vederea activă a desktopului Explorer-ului (`IShellWindows` →
`SWC_DESKTOP` → `IShellBrowser` → `QueryActiveShellView`). E o interfață COM documentată,
cross-process: nu se scrie memorie în `explorer.exe` și nu se injectează nimic.
Vezi [`DesktopIcons`](src/Frostpane.App/Desktop/DesktopIcons.cs).

**2. Unde se desenează panoul.** Prima variantă punea panoul ca fereastră-copil în ierarhia
desktopului, sub `SysListView32`, ca iconițele native ale Explorer-ului să rămână deasupra. Merge,
dar **nu cât timp rulează un wallpaper compus pe GPU** (Wallpaper Engine și similare): am testat
parentare în `Progman`, în `SHELLDLL_DefView` și în `WorkerW`-ul wallpaper-ului, cu fereastră WPF cu
transparență per-pixel, WPF opacă și GDI pură — niciuna nu apare pe ecran. Aceeași fereastră,
top-level, se vede impecabil.

Deci panourile sunt **ferestre top-level**, fixate pe ultima poziție în Z (`WM_WINDOWPOSCHANGING`
rescrie fiecare schimbare de Z în `HWND_BOTTOM`) și fără activare (`WS_EX_NOACTIVATE`), ca un click
în panou să nu fure focusul. Fiindcă shell-ul nu poate desena peste ele, iconițele revendicate de un
panou sunt **parcate în afara ecranului** (y = 30000) și redesenate de noi, cu iconițele și
miniaturile reale obținute prin `IShellItemImageFactory`. Iconițele pe care nu le revendică niciun
panou rămân în grija shell-ului, exact ca pe un desktop gol.

**3. Blur peste wallpaper animat.** Niciun API de blur al DWM nu vede conținutul unui wallpaper
animat: nici atributul acrylic din Windows 11 (`DWMWA_SYSTEMBACKDROP_TYPE`), nici vechea politică de
accent (`SetWindowCompositionAttribute`). Ambele eșantionează bitmap-ul static de wallpaper și redau
o culoare plată — măsurat: **0 din 5225 de pixeli** din interiorul unui panou se schimbau în 700 ms,
cât timp wallpaper-ul din jur se schimba vizibil. De aceea uneltele de acest fel blurează în general
doar wallpaper static.

Soluția e captură proprie: `Windows.Graphics.Capture` pe fereastra `Progman`, care conține orice
desenează fundalul. Panourile fiind top-level nu fac parte din `Progman`, deci nu apar niciodată în
propriul lor fundal. Cadrul e redus pe GPU prin mip-mapping (nivelul 4, adică 1/16), citit înapoi la
~53 KB, trecut printr-un box blur separabil și decupat per panou. După implementare, **2340 din 3344
de pixeli** se schimbă în 800 ms: blur real, pe conținut animat.
Vezi [`WallpaperCapture`](src/Frostpane.App/Desktop/WallpaperCapture.cs).

Captura e legată de fereastra desktopului care exista când a pornit, iar acea fereastră poate fi
înlocuită fără ca Explorer să repornească — o schimbare de motor de wallpaper o face. De aceea
există o supraveghere care o reconstruiește când nu mai livrează cadre, cu pauză între încercări:
cadrele se opresc și când nimic nu redesenează desktopul, de pildă un wallpaper animat pus pe pauză
în spatele unui joc pe tot ecranul. Starea capturii se vede în fereastra de setări.

Direct3D e apelat prin vtable, nu prin RCW-uri: dispozitivul se creează pe firul UI dar cadrele
sosesc pe fir MTA, iar obiectele D3D11 nu se marshalează între apartamente COM — orice apel din
apartamentul greșit eșuează cu `E_NOINTERFACE`.

## Siguranță

Iconițele parcate în afara ecranului ar arăta ca șterse dacă aplicația ar dispărea cu ele acolo.
Trei plase de siguranță:

- la ieșire, toate iconițele sunt returnate desktopului;
- la fiecare ciclu, orice iconiță parcată pe care n-o revendică niciun panou e adusă înapoi;
- meniul din tray are **Eliberează toate iconițele**.

## Detalii de implementare care nu se văd

- **Interfața aplicației e în engleză**; documentația a rămas în română.
- Un panou e o fereastră `WS_EX_NOACTIVATE` fixată la baza Z-order-ului, ca un click în el să nu
  fure focusul. De aici două consecințe: numele nu se poate edita pe loc, fiindcă o astfel de
  fereastră nu poate ține focusul de tastatură — redenumirea se face într-un dialog; și meniul
  contextual e un `ContextMenu` WPF, nu unul WinForms, fiindcă un `ContextMenuStrip` afișat de o
  aplicație care nu e în prim-plan se închide în aceeași clipă în care se deschide.

## Limitări cunoscute

- Panourile acoperă iconițele native aflate sub ele; în practică nu se vede, fiindcă orice iconiță
  ajunsă sub un panou e adoptată de el la următorul ciclu.
- Portalurile sunt doar de citire: din ele nu se poate trage un fișier afară.
- Meniul contextual e unul propriu, cu verbele shell-ului, nu meniul complet `IContextMenu` al
  Explorer-ului (fără intrările adăugate de aplicații terțe).
- Aplicația nu e semnată digital, deci SmartScreen va avertiza la prima rulare a installer-ului
  („Windows protected your PC" → *More info* → *Run anyway").
- Consumă în jur de 260 MB memorie privată, stabil: captura de wallpaper ține o textură cât întreg
  desktopul virtual. Fără blur ar fi o fracțiune din asta.
- Intrările din meniul desktopului sunt verbe clasice de shell. Dacă altceva a preluat meniul de
  click dreapta al desktopului — unele dock-uri și utilitare fac asta — s-ar putea să nu apară;
  crearea din meniul tray funcționează oricum.

## tools/DesktopProbe

Utilitar de diagnostic separat: listează ierarhia de ferestre a desktopului, starea vederii shell,
și pozițiile iconițelor; `move <index> <x> <y>` mută o iconiță. A fost instrumentul cu care s-au
validat toate ipotezele de mai sus și rămâne util când ceva se comportă ciudat.

```bash
dotnet run --project tools/DesktopProbe -- dump
```
