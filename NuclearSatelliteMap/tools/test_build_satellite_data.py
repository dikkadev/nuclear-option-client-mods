"""Unit checks; run with the producer's uv interpreter: python -I this_file.py.

Also discoverable by pytest. All image/geometry fixtures are generated in memory.
Only the tiny container test uses a TemporaryDirectory; it removes its output.
"""
import importlib.util
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch
import zlib

import numpy as np
from PIL import Image

_spec = importlib.util.spec_from_file_location("satellite_producer", Path(__file__).with_name("build_satellite_data.py"))
sat = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(sat)


class FormatTests(unittest.TestCase):
    def test_header_bytes_and_utf8_lengths(self):
        header = sat.make_header(sat.MAPS["heartland"], 8192, 8192, "sea 0 m; é", 12345)
        self.assertEqual(header[:8], b"NOSATMP1")
        fields = struct.unpack_from("<8s6I2f2IQIQ2I", header)
        self.assertEqual(fields[1] % 16, 0)
        self.assertEqual(fields[1], len(header))
        self.assertEqual(fields[2:7], (3,1,8192,8192,14))
        self.assertEqual(fields[7:11], (81920.,81920.,4096,4096))
        self.assertEqual(fields[11], sat.bc3_size(8192,8192))
        self.assertEqual(fields[12:14], (1,12345))
        self.assertEqual(struct.unpack_from("<2I",header,68),(1,1))
        self.assertEqual(fields[14:16],(1,1))
        self.assertEqual(sat.FIXED.size,76)
        offset = 76
        for expected in ("heartland-46396","Heartland","sea 0 m; é"):
            length, = struct.unpack_from("<H", header, offset)
            offset += 2
            self.assertEqual(header[offset:offset+length], expected.encode("utf-8"))
            offset += length
        self.assertEqual(header[offset:], bytes(len(header)-offset))

    def test_default_size_tile_grids_and_memory_bound(self):
        self.assertEqual(sat.tile_layout(16384,16384),(1,1,16384,16384))
        self.assertEqual(sat.tile_layout(32768,16384),(2,1,16384,16384))
        self.assertEqual(sat.tile_layout(65536,32768),(4,2,16384,16384))
        for w,h in ((16384,16384),(32768,16384),(16384,32768)):
            columns,rows,tw,th = sat.tile_layout(w,h)
            fields = sat.FIXED.unpack_from(sat.make_header(sat.MAPS["ignus"],w,h,"test"))
            self.assertEqual(fields[4:7],(w,h,sat.mip_count(tw,th)))
            self.assertEqual(fields[11],columns*rows*sat.bc3_size(tw,th))
            self.assertEqual(fields[14:16],(columns,rows))
            self.assertLess(sat.pyramid_bytes(w,h,sat.mip_count(tw,th)),5*1024**3)
        for w,h in ((0,4),(32772,16384),(32768,12000)):
            with self.assertRaises(ValueError):
                sat.tile_layout(w,h)

    def test_size_matches_literal_csharp_formula(self):
        for w,h in ((1,1),(2,1),(4,4),(12,8),(8192,8192),(16384,8192)):
            size,count = max(w,h),1
            while size > 1:
                size >>= 1
                count += 1
            expected = 0
            for level in range(count):
                mw,mh = max(1,w>>level),max(1,h>>level)
                expected += ((mw+3)//4)*((mh+3)//4)*16
            self.assertEqual(sat.mip_count(w,h),count)
            self.assertEqual(sat.bc3_size(w,h),expected)

    def test_alpha_bit_packing_and_constant_blocks(self):
        pixels = np.array([[255,0,219,182,146,109,73,36]*2,[42]*16],np.uint8)
        alpha = sat.encode_alpha(pixels)
        self.assertEqual(tuple(alpha[0,:2]),(255,0))
        bits = int.from_bytes(alpha[0,2:].tobytes(),"little")
        self.assertEqual([(bits>>(3*i))&7 for i in range(16)],[0,1,2,3,4,5,6,7]*2)
        self.assertEqual(alpha[1].tobytes(),bytes([42,42])+bytes(6))

    def test_color_endpoint_swap_and_constant_indices(self):
        blocks = np.array([list(bytes(8)+struct.pack("<HHI",1,65534,0x1b1b1b1b)),
                           list(bytes(8)+struct.pack("<HHI",123,123,0xffffffff))],np.uint8)
        sat.canonicalize_color(blocks)
        self.assertEqual(struct.unpack("<HHI",blocks[0,8:].tobytes()),(65534,1,0x4e4e4e4e))
        self.assertEqual(blocks[1,12:].tobytes(),bytes(4))

    def test_all_blocks_and_tiny_mips(self):
        rng = np.random.default_rng(937)
        for h,w in ((1,1),(1,2),(3,5),(4,4),(12,20)):
            image = rng.integers(0,256,(h,w,4),dtype=np.uint8)
            encoded = sat.encode_bc3(image)
            self.assertEqual(len(encoded),((w+3)//4)*((h+3)//4)*16)
            sat.assert_canonical(encoded)
            self.assertEqual(sat.decode_reference(encoded,w,h).shape,image.shape)

    def test_codec_roundtrip_independent_decode_and_psnr(self):
        sat.codec_probe()

    def test_container_full_chain_bottom_first_and_preserve_existing(self):
        image = np.zeros((8,12,4),np.uint8)
        image[:4] = (255,0,0,255)  # north
        image[4:] = (0,0,255,0)  # south
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)/"test.nosat"
            sat.write_texture(path,sat.MAPS["heartland"],image,"unit test")
            stored_file = path.read_bytes()
            header_size, = struct.unpack_from("<I",stored_file,8)
            data_length, compression, stored = struct.unpack_from("<QIQ",stored_file,48)
            self.assertEqual((data_length, compression), (sat.bc3_size(12,8), 1))
            self.assertEqual(len(stored_file),header_size+stored)
            # Raw deflate, as read by .NET DeflateStream; expand to the uncompressed layout.
            data = stored_file[:header_size] + zlib.decompress(stored_file[header_size:], -15)
            self.assertEqual(len(data),header_size+sat.bc3_size(12,8))
            decoded = sat.decode_reference(data[header_size:header_size+96],12,8)
            self.assertEqual(tuple(decoded[0,0]),(0,0,255,0))
            self.assertEqual(tuple(decoded[-1,0]),(255,0,0,255))
            offset = header_size
            for i in range(sat.mip_count(12,8)):
                w,h = max(1,12>>i),max(1,8>>i)
                count = ((w+3)//4)*((h+3)//4)*16
                sat.assert_canonical(data[offset:offset+count])
                offset += count
            self.assertEqual(offset,len(data))
            # v3's 1x1 payload is exactly the former v2 full-image layout.
            expected = bytearray()
            parent = image
            for _ in range(sat.mip_count(12,8)):
                pixels = np.clip(np.rint(parent[::-1]),0,255).astype(np.uint8)
                expected.extend(sat.encode_bc3(pixels))
                parent = sat.next_mip(parent)
            self.assertEqual(data[header_size:],expected)
            with self.assertRaises(FileExistsError):
                sat.write_texture(path,sat.MAPS["heartland"],image,"preserve")
            self.assertEqual(path.read_bytes(),stored_file)

    def test_tiled_full_image_mips_halves_and_seam_continuity(self):
        # Four distinct quadrants verify west/east and north/south independently.
        image = np.empty((8,16,4),np.uint8)
        image[:4,:8] = (255,0,0,255)
        image[4:,:8] = (0,255,0,255)
        image[:4,8:] = (0,0,255,0)
        image[4:,8:] = (255,255,255,128)
        self.check_tiled_container(image,2,1,check_colors=True)
        # Vary color and alpha near/across the seam. Compare encoder INPUT too,
        # so lossy BC3 cannot hide a wrong mip filter, crop, or float rounding.
        rng = np.random.default_rng(179)
        self.check_tiled_container(rng.integers(0,256,(8,16,4),dtype=np.uint8),2,1)
        self.check_tiled_container(rng.integers(0,256,(16,16,4),dtype=np.uint8),2,2)

    def check_tiled_container(self,image,columns,rows,check_colors=False):
        full = [image]
        for _ in range(3):
            full.append(sat.next_mip(full[-1]))
        inputs = []
        original_encode = sat.encode_bc3
        def record(pixels):
            inputs.append(pixels.copy())
            return original_encode(pixels)
        with tempfile.TemporaryDirectory() as directory, patch.object(sat,"MAX_TILE_SIDE",8):
            path = Path(directory)/"tiled.nosat"
            with patch.object(sat,"encode_bc3",side_effect=record):
                sat.write_texture(path,sat.MAPS["ignus"],image,"tiled unit test")
            container = path.read_bytes()
            fields = sat.FIXED.unpack_from(container)
            self.assertEqual(fields[4:7],(image.shape[1],image.shape[0],4))
            self.assertEqual(fields[14:16],(columns,rows))
            self.assertEqual(fields[11],columns*rows*sat.bc3_size(8,8))
            self.assertEqual(fields[12],1)
            self.assertEqual(len(container),fields[1]+fields[13])
            payload = zlib.decompress(container[fields[1]:],-15)
            self.assertEqual(len(payload),fields[11])
            offset = index = 0
            for row in range(rows):
                for column in range(columns):
                    for k,parent in enumerate(full):
                        side = 8 >> k
                        region = parent[row*side:(row+1)*side,column*side:(column+1)*side]
                        pixels = np.clip(np.rint(region[::-1]),0,255).astype(np.uint8)
                        np.testing.assert_array_equal(inputs[index],pixels)
                        encoded = original_encode(pixels)
                        actual = payload[offset:offset+len(encoded)]
                        self.assertEqual(actual,encoded)
                        decoded = sat.decode_reference(actual,side,side)[::-1]
                        expected = sat.decode_reference(encoded,side,side)[::-1]
                        np.testing.assert_array_equal(decoded,expected)
                        if k == 0 and check_colors:
                            # Solid 4x4 quadrants round-trip exact primary RGB.
                            self.assertLessEqual(int(np.abs(decoded.astype(int)-region.astype(int)).max()),1)
                        offset += len(encoded)
                        index += 1
            self.assertEqual(offset,len(payload))
            self.assertEqual(index,len(inputs))


class RasterTests(unittest.TestCase):
    def test_north_up_world_space_max_height_and_chunk_boundaries(self):
        # A plane with height=Z: its northern half must be land.
        vertices = np.array([[-2,-2,-2],[2,-2,-2],[2,2,2],[-2,2,2],
                             [-2,3,-2],[0,3,-2],[-2,3,0]],np.float32)
        faces = np.array([[0,1,2],[0,2,3],[4,5,6]],np.int32)
        depth = np.zeros((4,4),np.uint8)
        ids = np.arange(3,dtype=np.int32)
        whole,uncovered = sat.raster_chunk(vertices,faces,ids,depth,0,0,8,8,8,8,4.,4.,4,0.)
        self.assertEqual(uncovered,0)
        self.assertTrue(np.all(whole[:4] == 255))
        self.assertEqual(whole[-1,-1],0)
        self.assertEqual(whole[-1,0],255)  # overlapping high triangle wins
        pointers,ids = sat.bin_triangles(vertices,faces,8,8,4.,4.,3)
        assembled = np.empty((8,8),np.uint8)
        for cell in range(9):
            x,y = cell%3*3,cell//3*3
            tile,_ = sat.raster_chunk(vertices,faces,ids[pointers[cell]:pointers[cell+1]],
                                      depth,x,y,min(3,8-x),min(3,8-y),8,8,4.,4.,4,0.)
            assembled[y:y+tile.shape[0],x:x+tile.shape[1]] = tile
        np.testing.assert_array_equal(assembled,whole)

    def test_bilinear_fallback_only_on_uncovered_samples(self):
        vertices = np.empty((0,3),np.float32)
        faces = np.empty((0,3),np.int32)
        depth = np.array([[255,0],[255,0]],np.uint8)
        mask,count = sat.raster_chunk(vertices,faces,np.empty(0,np.int32),depth,
                                     0,0,4,4,4,4,4.,4.,2,0.)
        self.assertEqual(count,64)
        # First two subpixel centers clamp to the western source pixel; the
        # next output column interpolates below 240 (nearest would stay land).
        np.testing.assert_array_equal(mask,np.tile([255,0,0,0],(4,1)).astype(np.uint8))

    def test_lanczos_strips_match_full_image_without_seams(self):
        rng = np.random.default_rng(1234)
        source = rng.integers(0,256,(750,800,3),dtype=np.uint8)
        full = Image.fromarray(source)
        class MemoryTiles(sat.TileSource):
            def fetch(self,xy):
                x,y = xy
                # Just like a tile's 512 logical pixels after removing gutters.
                return xy,full.crop((x*64,y*64,(x+1)*64,(y+1)*64)),False
        tiles = MemoryTiles.__new__(MemoryTiles)
        tiles.width,tiles.height,tiles.size = 800,750,64
        tiles.level = {"columns":13,"rows":12}
        tiles.cache,tiles.missing = {},0
        output = np.empty((600,640,4),np.uint8)
        tiles.render(output)
        expected = np.asarray(full.resize((640,600),Image.Resampling.LANCZOS))
        np.testing.assert_array_equal(output[:,:,:3],expected)


class MipTests(unittest.TestCase):
    def test_alpha_weighted_rgb_and_plain_water_rgb(self):
        image = np.array([[[200,0,0,255],[0,0,200,0],[0,100,0,0],[0,0,100,0]],
                          [[200,0,0,255],[0,0,200,0],[0,100,0,0],[0,0,100,0]]],np.float32)
        mip = sat.next_mip(image)
        half = float(sat.linear_to_srgb(sat.srgb_to_linear(np.float32(100))/2))
        np.testing.assert_allclose(mip,np.array([[[200,0,0,127.5],[0,half,half,0]]],np.float32),atol=.01)
        last = sat.next_mip(mip)
        np.testing.assert_allclose(last,np.array([[[200,0,0,63.75]]],np.float32),atol=.01)

    def test_rgb_averages_in_linear_light(self):
        image = np.array([[[0,0,0,255],[255,255,255,255]],[[0,0,0,255],[255,255,255,255]]],np.float32)
        np.testing.assert_allclose(sat.next_mip(image)[0,0,:3],[187.516]*3,atol=.01)


if __name__ == "__main__":
    unittest.main(verbosity=2)
