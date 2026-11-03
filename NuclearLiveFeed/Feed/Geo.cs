using System;

namespace NuclearLiveFeed.Feed
{
    public readonly struct Vec3
    {
        public readonly double X, Y, Z;
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public static readonly Vec3 Zero = default;
        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
        public static Vec3 operator *(Vec3 a, double s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public double Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;
        public Vec3 Cross(Vec3 b) => new Vec3(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public Vec3 Normalized() { double l = Length; return l > 1e-12 ? this * (1 / l) : Zero; }
        public override string ToString() => "(" + X.ToString("0.###") + ", " + Y.ToString("0.###") + ", " + Z.ToString("0.###") + ")";
    }

    /// <summary>
    /// Places a fictional map on WGS84. Game X/Z/Y (east/north/up meters from the map center) become ENU at the anchor,
    /// then ECEF for DIS and latitude/longitude/height above the ellipsoid for CoT.
    /// </summary>
    public sealed class GeoAnchor
    {
        private const double A = 6378137.0, F = 1 / 298.257223563, E2 = F * (2 - F);
        public double Latitude { get; }
        public double Longitude { get; }
        public double Height { get; }
        private readonly Vec3 origin, east, north, up;

        public GeoAnchor(double latitude, double longitude, double height)
        {
            Latitude = latitude; Longitude = longitude; Height = height;
            double lat = latitude * Math.PI / 180, lon = longitude * Math.PI / 180;
            double sLat = Math.Sin(lat), cLat = Math.Cos(lat), sLon = Math.Sin(lon), cLon = Math.Cos(lon);
            east = new Vec3(-sLon, cLon, 0);
            north = new Vec3(-sLat * cLon, -sLat * sLon, cLat);
            up = new Vec3(cLat * cLon, cLat * sLon, sLat);
            origin = GeodeticToEcef(latitude, longitude, height);
        }

        /// <summary>ENU direction to ECEF direction.</summary>
        public Vec3 EnuToEcefVector(Vec3 enu) => east * enu.X + north * enu.Y + up * enu.Z;

        public Vec3 EnuToEcef(Vec3 enu) => origin + EnuToEcefVector(enu);

        public Vec3 EcefToEnu(Vec3 ecef)
        {
            var d = ecef - origin;
            return new Vec3(d.Dot(east), d.Dot(north), d.Dot(up));
        }

        public static Vec3 GeodeticToEcef(double latitude, double longitude, double height)
        {
            double lat = latitude * Math.PI / 180, lon = longitude * Math.PI / 180;
            double sLat = Math.Sin(lat), cLat = Math.Cos(lat);
            double n = A / Math.Sqrt(1 - E2 * sLat * sLat);
            return new Vec3((n + height) * cLat * Math.Cos(lon), (n + height) * cLat * Math.Sin(lon), (n * (1 - E2) + height) * sLat);
        }

        /// <summary>ECEF to latitude/longitude degrees and height above the ellipsoid (Bowring, iterated).</summary>
        public static (double Latitude, double Longitude, double Height) EcefToGeodetic(Vec3 p)
        {
            double lon = Math.Atan2(p.Y, p.X);
            double r = Math.Sqrt(p.X * p.X + p.Y * p.Y);
            double lat = Math.Atan2(p.Z, r * (1 - E2)), h = 0;
            for (int i = 0; i < 6; i++)
            {
                double s = Math.Sin(lat);
                double n = A / Math.Sqrt(1 - E2 * s * s);
                h = r / Math.Cos(lat) - n;
                lat = Math.Atan2(p.Z, r * (1 - E2 * n / (n + h)));
            }
            return (lat * 180 / Math.PI, lon * 180 / Math.PI, h);
        }

        /// <summary>
        /// DIS Euler angles (psi, theta, phi) of a body whose forward, right and down axes are given in ENU.
        /// R(ecef from body) = Rz(psi) Ry(theta) Rx(phi).
        /// </summary>
        public (float Psi, float Theta, float Phi) EulerFromEnuAxes(Vec3 forward, Vec3 right, Vec3 down)
        {
            var x = EnuToEcefVector(forward);
            var y = EnuToEcefVector(right);
            var z = EnuToEcefVector(down);
            double psi = Math.Atan2(x.Y, x.X);
            double theta = Math.Asin(Math.Max(-1, Math.Min(1, -x.Z)));
            double phi = Math.Atan2(y.Z, z.Z);
            return ((float)psi, (float)theta, (float)phi);
        }

        /// <summary>Body forward axis in ENU from DIS Euler angles (viewer-side inverse, used by tests).</summary>
        public Vec3 ForwardEnuFromEuler(double psi, double theta)
        {
            var x = new Vec3(Math.Cos(psi) * Math.Cos(theta), Math.Sin(psi) * Math.Cos(theta), -Math.Sin(theta));
            return new Vec3(x.Dot(east), x.Dot(north), x.Dot(up));
        }
    }
}
