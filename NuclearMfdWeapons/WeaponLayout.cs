using UnityEngine;
namespace NuclearMfdWeapons
{
    internal sealed class WeaponLayout
    {
        public Vector2 flarePos, jammerPos, lineStart, lineEnd, weaponNamePos, weaponAmmoPos, weaponImagePos;
        public int flareFont, jammerFont, weaponNameFont, weaponAmmoFont;
        public bool rotateWeaponImage = false, removeOriginalMFDContent = true;
        public float imageScaleFactor = 0.6f;
        public WeaponLayout(string platformName)
        {
            switch (platformName)
            {
                case "CI-22 Cricket":
                    flarePos = new Vector2(0, -40);
                    jammerPos = new Vector2(0, -80);
                    lineStart = new Vector2(-60, 0);
                    lineEnd = new Vector2(60, 0);
                    weaponNamePos = new Vector2(0, 60);
                    weaponAmmoPos = new Vector2(0, 30);
                    weaponImagePos = new Vector2(0, 80);
                    flareFont = 30;
                    jammerFont = 30;
                    weaponNameFont = 18;
                    weaponAmmoFont = 40;
                    break;
                case "SAH-46 Chicane":
                    flarePos = new Vector2(-40, -105);
                    jammerPos = new Vector2(40, -105);
                    lineStart = new Vector2(-80, -10);
                    lineEnd = new Vector2(80, -10);
                    weaponNamePos = new Vector2(0, -45);
                    weaponAmmoPos = new Vector2(0, -70);
                    weaponImagePos = new Vector2(0, -25);
                    flareFont = 18;
                    jammerFont = 18;
                    weaponNameFont = 20;
                    weaponAmmoFont = 35;
                    imageScaleFactor = 0.4f; // Scale the image for SAH-46 Chicane
                    removeOriginalMFDContent = false; // Do not remove original MFD content for SAH-46 Chicane
                    rotateWeaponImage = false;
                    break;
                case "T/A-30 Compass":
                case "VT-7 Vagrant":
                    flarePos = new Vector2(0, -40);
                    jammerPos = new Vector2(0, -80);
                    lineStart = new Vector2(-60, 0);
                    lineEnd = new Vector2(60, 0);
                    weaponNamePos = new Vector2(0, 60);
                    weaponAmmoPos = new Vector2(0, 30);
                    weaponImagePos = new Vector2(0, 80);
                    flareFont = 30;
                    jammerFont = 30;
                    weaponNameFont = 18;
                    weaponAmmoFont = 40;
                    break;
                case "FS-12 Revoker":
                    flarePos = new Vector2(0, -40);
                    jammerPos = new Vector2(0, -80);
                    lineStart = new Vector2(-100, -10);
                    lineEnd = new Vector2(100, -10);
                    weaponNamePos = new Vector2(0, 50);
                    weaponAmmoPos = new Vector2(0, 20);
                    weaponImagePos = new Vector2(0, 80);
                    flareFont = 30;
                    jammerFont = 30;
                    weaponNameFont = 25;
                    weaponAmmoFont = 35;
                    imageScaleFactor = 0.8f; // Scale the image for FS-12 Revoker
                    break;
                case "FS-20 Vortex":
                    flarePos = new Vector2(-60, -70);
                    jammerPos = new Vector2(60, -70);
                    lineStart = new Vector2(-120, -20);
                    lineEnd = new Vector2(120, -20);
                    weaponNamePos = new Vector2(0, 70);
                    weaponAmmoPos = new Vector2(60, 20);
                    weaponImagePos = new Vector2(-60, 20);
                    flareFont = 35;
                    jammerFont = 35;
                    weaponNameFont = 30;
                    weaponAmmoFont = 55;
                    imageScaleFactor = 0.7f; // Scale the image for FS-20 Vortex
                    break;
                case "KR-67 Ifrit":
                    flarePos = new Vector2(-80, -70);
                    jammerPos = new Vector2(70, -70);
                    lineStart = new Vector2(-100, -20);
                    lineEnd = new Vector2(100, -20);
                    weaponNamePos = new Vector2(0, 70);
                    weaponAmmoPos = new Vector2(80, 20);
                    weaponImagePos = new Vector2(-70, 20);
                    flareFont = 45;
                    jammerFont = 45;
                    weaponNameFont = 45;
                    weaponAmmoFont = 55;
                    imageScaleFactor = 0.8f; // Scale the image for KR-67 Ifrit
                    break;
                case "VL-49 Tarantula":
                    flarePos = new Vector2(105, 40);
                    jammerPos = new Vector2(105, -40);
                    lineStart = new Vector2(30, -60);
                    lineEnd = new Vector2(30, 60);
                    weaponNamePos = new Vector2(-60, -10);
                    weaponAmmoPos = new Vector2(-60, -50);
                    weaponImagePos = new Vector2(-60, 40);
                    flareFont = 25;
                    jammerFont = 25;
                    weaponNameFont = 18;
                    weaponAmmoFont = 40;
                    imageScaleFactor = 0.6f;
                    break;
                case "EW-1 Medusa":
                    flarePos = new Vector2(-60, -70);
                    jammerPos = new Vector2(60, -70);
                    lineStart = new Vector2(-100, -20);
                    lineEnd = new Vector2(100, -20);
                    weaponNamePos = new Vector2(0, 70);
                    weaponAmmoPos = new Vector2(80, 20);
                    weaponImagePos = new Vector2(-60, 20);
                    flareFont = 35;
                    jammerFont = 35;
                    weaponNameFont = 30;
                    weaponAmmoFont = 50;
                    imageScaleFactor = 0.6f; // Scale the image for EW-1 Medusa
                    break;
                case "SFB-81":
                    flarePos = new Vector2(60, -40);
                    jammerPos = new Vector2(60, -80);
                    lineStart = new Vector2(20, 0);
                    lineEnd = new Vector2(100, 0);
                    weaponNamePos = new Vector2(60, 80);
                    weaponAmmoPos = new Vector2(60, 40);
                    weaponImagePos = new Vector2(-60, 0);
                    flareFont = 30;
                    jammerFont = 30;
                    weaponNameFont = 25;
                    weaponAmmoFont = 40;
                    rotateWeaponImage = true; // Rotate the weapon image for SFB-81
                    imageScaleFactor = 0.8f; // Scale the image for SFB-81
                    break;
                case "UH-80 Ibis":
                    flarePos = new Vector2(-60, -70);
                    jammerPos = new Vector2(60, -70);
                    lineStart = new Vector2(-100, -20);
                    lineEnd = new Vector2(100, -20);
                    weaponNamePos = new Vector2(0, 70);
                    weaponAmmoPos = new Vector2(80, 20);
                    weaponImagePos = new Vector2(-60, 20);
                    flareFont = 35;
                    jammerFont = 35;
                    weaponNameFont = 35;
                    weaponAmmoFont = 50;
                    imageScaleFactor = 0.6f; // Scale the image for EW-1 Medusa
                    break;
                case "A-19 Brawler":
                    flarePos = new Vector2(0, -40);
                    jammerPos = new Vector2(0, -75);
                    lineStart = new Vector2(-80, -10);
                    lineEnd = new Vector2(80, -10);
                    weaponNamePos = new Vector2(0, 45);
                    weaponAmmoPos = new Vector2(0, 15);
                    weaponImagePos = new Vector2(0, 75);
                    flareFont = 30;
                    jammerFont = 30;
                    weaponNameFont = 25;
                    weaponAmmoFont = 40;
                    break;
                case "Alkyon AB-4":
                case "AB-4 Alkyon":
                case "FastBomber1":
                    flarePos = new Vector2(0, -160);
                    jammerPos = new Vector2(0, -300);
                    lineStart = new Vector2(-320, -40);
                    lineEnd = new Vector2(320, -40);
                    weaponNamePos = new Vector2(0, 180);
                    weaponAmmoPos = new Vector2(0, 60);
                    weaponImagePos = new Vector2(0, 300);
                    flareFont = 120;
                    jammerFont = 120;
                    weaponNameFont = 100;
                    weaponAmmoFont = 160;
                    imageScaleFactor = 2.0f;
                    break;
                default:
                    flarePos = new Vector2(0, -40);
                    jammerPos = new Vector2(0, -80);
                    lineStart = new Vector2(-60, -7);
                    lineEnd = new Vector2(60, -7);
                    weaponNamePos = new Vector2(0, 60);
                    weaponAmmoPos = new Vector2(0, 22);
                    weaponImagePos = new Vector2(0, 87);
                    flareFont = 35;
                    jammerFont = 35;
                    weaponNameFont = 25;
                    weaponAmmoFont = 45;
                    break;
            }


        }
    }
}
