"""Converts the IS-Net model to half precision, keeping float32 input and output: python isnet-fp16.py in.onnx out.onnx"""
import sys

import onnx
from onnxruntime.transformers import float16

model = onnx.load(sys.argv[1])
onnx.save(float16.convert_float_to_float16(model, keep_io_types=True), sys.argv[2])
