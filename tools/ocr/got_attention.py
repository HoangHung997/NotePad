"""Equivalent bounded-query attention for GOT's CPU vision encoder.

The upstream eager path simultaneously allocates several full N x N attention
matrices. Processing independent query rows in chunks retains every key/value and
relative position, avoiding that peak without resizing pages or truncating text.
"""
import types


def chunked_forward(self, hidden_states, output_attentions=None):
    import torch
    batch, height, width, _ = hidden_states.shape
    count = height * width
    qkv = self.qkv(hidden_states).reshape(batch, count, 3, self.num_attention_heads, -1).permute(2, 0, 3, 1, 4)
    query, key, value = qkv.reshape(3, batch * self.num_attention_heads, count, -1).unbind(0)
    if self.use_rel_pos:
        rh = self.get_rel_pos(height, height, self.rel_pos_h)
        rw = self.get_rel_pos(width, width, self.rel_pos_w)
    outputs, observed = [], []
    for start in range(0, count, 256):
        q = query[:, start:start+256]
        weights = (q * self.scale) @ key.transpose(-2, -1)
        if self.use_rel_pos:
            positions = torch.arange(start, start+q.shape[1], device=query.device)
            rel_h = torch.einsum("bqc,qkc->bqk", q, rh[positions // width])
            rel_w = torch.einsum("bqc,qkc->bqk", q, rw[positions % width])
            weights = weights + (rel_h[:, :, :, None] + rel_w[:, :, None, :]).reshape_as(weights)
        weights = torch.nn.functional.softmax(weights, dtype=torch.float32, dim=-1).to(query.dtype)
        probs = torch.nn.functional.dropout(weights, p=self.dropout, training=self.training)
        outputs.append(probs @ value)
        if output_attentions: observed.append(weights)
    result = torch.cat(outputs, dim=1).reshape(batch, self.num_attention_heads, height, width, -1)
    result = result.permute(0, 2, 3, 1, 4).reshape(batch, height, width, -1)
    return self.proj(result), torch.cat(observed, dim=1) if observed else None


def install(model):
    from transformers.models.got_ocr2.modeling_got_ocr2 import GotOcr2VisionAttention
    for module in model.modules():
        if isinstance(module, GotOcr2VisionAttention):
            module.forward = types.MethodType(chunked_forward, module)
