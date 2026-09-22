"""Numerical equivalence of bounded GOT attention; no model download or user data."""
import unittest
import torch
from transformers.models.got_ocr2.configuration_got_ocr2 import GotOcr2VisionConfig
from transformers.models.got_ocr2.modeling_got_ocr2 import GotOcr2VisionAttention
from got_attention import chunked_forward


class GotAttentionTests(unittest.TestCase):
    def test_same_outputs_and_attention_for_multiple_chunks_and_relative_positions(self):
        torch.set_num_threads(2)
        for relative in (False, True):
            for shape in ((1, 4, 4, 32), (2, 17, 18, 32), (1, 23, 23, 32)):
                with self.subTest(relative=relative, shape=shape):
                    torch.manual_seed(42)
                    config = GotOcr2VisionConfig(hidden_size=32, num_attention_heads=4,
                        image_size=32, patch_size=4, use_rel_pos=relative, attention_dropout=0)
                    layer = GotOcr2VisionAttention(config, 0).eval()
                    if relative:
                        torch.nn.init.normal_(layer.rel_pos_h)
                        torch.nn.init.normal_(layer.rel_pos_w)
                    value = torch.randn(shape)
                    with torch.inference_mode():
                        expected, attention = layer(value, output_attentions=True)
                        actual, chunked = chunked_forward(layer, value, output_attentions=True)
                        compact, absent = chunked_forward(layer, value)
                    torch.testing.assert_close(actual, expected, rtol=1e-5, atol=1e-6)
                    torch.testing.assert_close(chunked, attention, rtol=1e-5, atol=1e-6)
                    torch.testing.assert_close(compact, expected, rtol=1e-5, atol=1e-6)
                    self.assertIsNone(absent)


if __name__ == "__main__": unittest.main()
